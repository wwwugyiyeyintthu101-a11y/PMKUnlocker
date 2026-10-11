"""Fail-closed validation in the same DA session that performs firmware writes."""
import hashlib
import json
import os
import re
import shutil
import tempfile
import struct
import time
from contextlib import contextmanager
from pmk_sparse import inspect, expand, SparseStream
from pmk_gpt import prepare as prepare_gpt, refresh as refresh_gpt


@contextmanager
def bulk_firmware_transfer(handler):
    """Batch USB host transfers without changing the DA's negotiated packet size.

    Only XFlash write-data parameters are batched. Other commands and serial
    transports retain the upstream implementation. Never retry a partial write.
    """
    from mtkclient.Library.DA.xflash.xflash_lib import DAXFlash
    from mtkclient.Library.Connection.usblib import UsbClass
    da = handler.mtk.daloader.da
    if not isinstance(da, DAXFlash) or not isinstance(handler.mtk.port.cdc, UsbClass):
        yield
        return
    original = da.send_param
    def send(params):
        if not (isinstance(params, list) and len(params) == 3 and
                params[0] == b'\0' * 4 and len(params[1]) == 4 and len(params[2]) >= 512):
            return original(params)
        for param in params:
            header = struct.pack('<III', da.cmd.MAGIC, da.data_type.DT_PROTOCOL_FLOW, len(param))
            if not da.usbwrite(header):
                return False
            for offset in range(0, len(param), 64 * 1024):
                if not da.usbwrite(param[offset:offset + 64 * 1024]):
                    return False
        status = da.status()
        if status != 0:
            da.error("Firmware bulk-transfer status: " + str(status))
            return False
        return True
    da.send_param = send
    print("PMK: XFlash USB firmware transfers batched at 64 KiB; DA checksum/status checks retained", flush=True)
    try:
        yield
    finally:
        da.send_param = original


@contextmanager
def prepared_images(handler, plan, filenames, cancelled):
    # Prepare EVERY sparse image before the first device write. No sparse container
    # is ever passed to the raw DA writer, even when a later image is malformed.
    metadata = [inspect(path, item["size"]) for path, item in zip(filenames, plan["items"])]
    from mtkclient.Library.DA.xflash.xflash_lib import DAXFlash
    if isinstance(handler.mtk.daloader.da, DAXFlash):
        streams = {}
        try:
            for path, item, (sparse, size) in zip(filenames, plan["items"], metadata):
                cancelled()
                if sparse:
                    stream = SparseStream(path, item["size"])
                    streams[os.path.normcase(os.path.realpath(path))] = stream
                    stream.validate(cancelled, lambda done, total:
                        print("PMK_PREP|Validating sparse " + item["name"] + "|%d|%d" % (done, total), flush=True))
            print("PMK_TIMING: Sparse streaming ready; no temporary raw images created", flush=True)
            yield filenames, streams
        finally:
            for stream in streams.values():
                stream.close()
        return
    required = sum(size for sparse, size in metadata if sparse)
    with tempfile.TemporaryDirectory(prefix="pmk-raw-") as directory:
        if required and shutil.disk_usage(directory).free < required + 64 * 1024 * 1024:
            raise ValueError("Not enough temporary disk space to expand sparse firmware (%d bytes required)" % required)
        prepared = []
        for index, (path, item, (sparse, size)) in enumerate(zip(filenames, plan["items"], metadata)):
            cancelled()
            if not sparse:
                prepared.append(path)
                continue
            subdir = os.path.join(directory, str(index))
            os.mkdir(subdir)
            target = os.path.join(subdir, os.path.basename(path))
            print("PMK: Preparing sparse %s -> raw (%d bytes); device write has not started" % (item["name"], size), flush=True)
            expand(path, target, item["size"], cancelled,
                   lambda done, total: print("PMK_PREP|Expanding " + item["name"] + "|%d|%d" % (done, total), flush=True))
            prepared.append(target)
        yield prepared, {}


BOOT_REGIONS = {
    "EMMC_BOOT1": ("boot1",),
    "EMMC_BOOT2": ("boot2",),
    "EMMC_BOOT1_BOOT2": ("boot1", "boot2"),
}


def prepare_boot_targets(handler, plan, item, filename):
    """Preserve the live EMMC_BOOT/BRLYT layout; never write bare SV5 at zero."""
    from mtkclient.Library.DA.xflash.xflash_lib import DAXFlash
    if not isinstance(handler.mtk.daloader.da, DAXFlash):
        raise ValueError("Preloader boot-region writing currently requires XFlash DA")
    if item["name"].lower() != "preloader" or item["start"] != 0:
        raise ValueError("Only preloader at scatter boot-region origin is supported")
    with open(filename, "rb") as stream:
        payload = stream.read(item["size"] + 1)
    if len(payload) < 0x38 or payload[:4] != b"MMM\x01":
        raise ValueError("Expected a raw SV5 preloader image (MMM header)")
    if struct.unpack_from("<I", payload, 0x20)[0] != len(payload):
        raise ValueError("Preloader declared length does not match file")
    padded_length = (len(payload) + 511) // 512 * 512
    targets = []
    for region in BOOT_REGIONS[item["region"].upper()]:
        capacity = int(getattr(handler.mtk.daloader.daconfig.storage.emmc, region + "_size", 0))
        size = item["size"]
        if capacity <= 0 or size <= 0 or size > capacity or size % 512:
            raise ValueError("Invalid live " + region + " capacity / scatter size")
        original = handler.mtk.daloader.readflash(0, size, "", region, display=False)
        if not isinstance(original, (bytes, bytearray)) or len(original) != size:
            raise ValueError("Cannot back up " + region + "; nothing written")
        os.makedirs(plan["backup"], exist_ok=True)
        backup = os.path.join(plan["backup"], region + ".bin")
        with open(backup, "xb") as stream:
            stream.write(original)
            stream.flush()
            os.fsync(stream.fileno())
        print("PMK_BOOT: Backup saved: " + backup, flush=True)
        if original[:9] != b"EMMC_BOOT":
            raise ValueError("Unrecognized " + region + " boot header; backup retained, nothing written")
        layout = struct.unpack_from("<I", original, 0x10)[0]
        if layout < 0x14 or layout + 16 > size or original[layout:layout + 5] != b"BRLYT":
            raise ValueError("Invalid " + region + " BRLYT header")
        offset = struct.unpack_from("<I", original, layout + 12)[0]
        if offset < layout + 16 or offset % 512 or offset + padded_length > size:
            raise ValueError("Preloader does not fit existing " + region + " layout")
        if original[offset:offset + 4] != b"MMM\x01":
            raise ValueError("Existing " + region + " preloader location is not recognized")
        targets.append((region, offset, padded_length))
    return targets


def validate(handler, plan, parttype, filenames, partitions):
    if parttype not in (None, "user"):
        raise ValueError("Firmware plan must enter through the user-area dispatcher")
    items = plan["items"]
    if partitions != [i["name"] for i in items] or len(filenames) != len(items):
        raise ValueError("Backend write request differs from reviewed plan")
    chip = handler.mtk.config.chipconfig.name
    if plan["platform"].upper() not in re.findall(r"MT\d+[A-Z]*", chip.upper()):
        raise ValueError("Connected chipset does not match firmware")
    storage = handler.mtk.daloader.daconfig.storage
    if str(storage.flashtype).lower() != "emmc" or plan["storage"].upper() != "EMMC":
        raise ValueError("No validated flashing profile for this storage type")
    capacity = int(storage.flashsize)
    page = int(handler.mtk.config.pagesize)
    if capacity <= 0 or page <= 0:
        raise ValueError("Device storage geometry unavailable")
    boot_targets = {}
    # Validate ALL targets before the first write, using this live DA connection.
    for item, filename in zip(items, filenames):
        if os.path.normcase(os.path.realpath(filename)) != os.path.normcase(os.path.realpath(item["path"])):
            raise ValueError("Image path changed after review")
        region = item["region"].upper()
        if region != "EMMC_USER" and region not in BOOT_REGIONS:
            raise ValueError("Unsupported partition region")
        start, size = item["start"], item["size"]
        if start < 0 or size <= 0 or start + size > capacity:
            raise ValueError("Partition outside supported device address range")
        if region == "EMMC_USER":
            found = handler.mtk.daloader.detect_partition(item["name"], parttype)
            if not found[0]:
                raise ValueError("Selected partition absent from connected device: " + item["name"])
            actual = found[1]
            actual_start, actual_size = actual.sector * page, actual.sectors * page
            detail = ("%s: scatter start=0x%x size=0x%x; device start=0x%x size=0x%x" %
                      (item["name"], start, size, actual_start, actual_size))
            if actual_start < 0 or actual_size <= 0 or actual_start + actual_size > capacity:
                raise ValueError("Device partition outside storage capacity: " + detail)
            # A larger userdata on another capacity variant is acceptable only at
            # the same start. Keep the image bounded by the reviewed scatter size;
            # never resize GPT or redirect a write to a different offset.
            larger_userdata = item["name"].lower() == "userdata" and actual_size >= size
            if actual_start != start or (actual_size != size and not larger_userdata):
                raise ValueError("Device partition layout differs from scatter: " + detail)
            if actual_size != size:
                print("PMK_LAYOUT: Larger userdata accepted; " + detail, flush=True)
        digest = hashlib.sha256()
        with open(filename, "rb") as stream:
            total = os.fstat(stream.fileno()).st_size
            done = 0
            last_report = time.monotonic()
            print("PMK_PREP|Checking " + item["name"] + "|0|%d" % total, flush=True)
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                if os.path.exists(plan["cancel"]):
                    raise ValueError("Cancelled during image validation; no write started")
                digest.update(chunk)
                done += len(chunk)
                if time.monotonic() - last_report >= 1:
                    print("PMK_PREP|Checking " + item["name"] + "|%d|%d" % (done, total), flush=True)
                    last_report = time.monotonic()
        if digest.hexdigest().lower() != item["hash"].lower():
            raise ValueError("Firmware image changed after review")
        if region in BOOT_REGIONS:
            boot_targets[item["name"]] = prepare_boot_targets(handler, plan, item, filename)
    return boot_targets


def install():
    raw = os.environ.get("PMK_FLASH_PLAN")
    if not raw:
        return
    plan = json.loads(raw)
    from mtkclient.Library.DA.mtk_da_handler import DaHandler
    original = DaHandler.da_write

    # A previous failed run can leave .state behind while the phone has reset.
    # Firmware always starts from a fresh BROM handshake, never cached DA state.
    # Retain this directory via the closure for the lifetime of this CLI process.
    session = tempfile.TemporaryDirectory(prefix="pmk-firmware-session-")

    def fresh_connect(handler, mtk, directory=None):
        print("PMK_SESSION: Starting fresh BROM handshake; power off and reconnect the phone in BROM mode", flush=True)
        mtk.reinited = False
        mtk.config.hwparam_path = session.name
        if not mtk.preloader.init(directory=session.name) or mtk.config.target_config is None:
            print("PMK_ERROR: Firmware connection failed; power off and reconnect in BROM mode. Nothing written.", flush=True)
            raise SystemExit(32)
        return mtk

    DaHandler.connect = fresh_connect

    def guarded(handler, parttype, filenames, partitions):
        preparation_started = time.monotonic()
        def cancelled():
            if os.path.exists(plan["cancel"]):
                print("PMK_ERROR: Firmware cancelled at partition boundary", flush=True)
                raise SystemExit(31)
        try:
            cancelled()
            boot_targets = validate(handler, plan, parttype, filenames, partitions)
            gpt_targets = prepare_gpt(handler, plan) if plan.get("refresh_gpt") else []
        except Exception as exc:
            print("PMK_ERROR: Firmware preflight blocked: " + str(exc), flush=True)
            raise SystemExit(32)
        try:
            with prepared_images(handler, plan, filenames, cancelled) as (ready, streams):
                print("PMK_TIMING: Validation/preparation: %.1f seconds" % (time.monotonic() - preparation_started), flush=True)
                writing_started = time.monotonic()
                print("PMK_PREFLIGHT_OK: live chipset, storage, GPT and image hashes checked", flush=True)
                if gpt_targets:
                    refresh_gpt(handler, gpt_targets, cancelled)
                    print("PMK_GPT: Primary and secondary refreshed; existing userdata capacity preserved", flush=True)
                for name, filename in zip(partitions, ready):
                    cancelled()
                    # Stop on a failed low-level write, even if upstream only prints an error.
                    writeflash = handler.mtk.daloader.writeflash
                    def checked_write(*args, **kwargs):
                        path = kwargs.get("filename")
                        stream = streams.get(os.path.normcase(os.path.realpath(path))) if path else None
                        if stream is not None:
                            stream.rewind()
                            kwargs = dict(kwargs, filename="", length=stream.size, wdata=stream)
                        result = writeflash(*args, **kwargs)
                        if stream is not None and stream.position != stream.size:
                            raise ValueError("Sparse stream was not fully transferred: " + name)
                        if not result:
                            print("PMK_ERROR: Firmware write failed: " + name, flush=True)
                            raise SystemExit(33)
                        return result
                    handler.mtk.daloader.writeflash = checked_write
                    try:
                        with bulk_firmware_transfer(handler):
                            if name in boot_targets:
                                for region, offset, length in boot_targets[name]:
                                    cancelled()
                                    print("PMK_BOOT: Writing preloader to %s at 0x%x" % (region, offset), flush=True)
                                    checked_write(addr=offset, length=length, filename=filename, parttype=region)
                                    actual = handler.mtk.daloader.readflash(offset, length, "", region, display=False)
                                    with open(filename, "rb") as stream:
                                        expected = stream.read().ljust(length, b"\0")
                                    if actual != expected:
                                        raise ValueError("Preloader readback mismatch: " + region + "; remaining writes stopped")
                                    print("PMK_BOOT: " + region + " preloader readback verified", flush=True)
                                print("Wrote %s to sector %d in selected boot regions (readback verified)." % (filename, boot_targets[name][0][1] // 512), flush=True)
                            else:
                                original(handler, parttype, [filename], [name])
                    finally:
                        handler.mtk.daloader.writeflash = writeflash
                    print("PMK_FLASH_WRITTEN: " + name, flush=True)
                print("PMK_TIMING: Device writes/readback: %.1f seconds" % (time.monotonic() - writing_started), flush=True)
                cancelled()
        except Exception as exc:
            print("PMK_ERROR: Firmware sparse preparation/write failed: " + str(exc), flush=True)
            raise SystemExit(32)
    DaHandler.da_write = guarded
