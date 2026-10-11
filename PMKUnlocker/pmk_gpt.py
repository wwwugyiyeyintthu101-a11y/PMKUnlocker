"""Refresh validated eMMC GPT copies without changing any partition capacity.

Layout/CRC fields follow UEFI 2.10 section 5.3. This is deliberately not a
scatter repartitioner: existing partition GUIDs, offsets and sizes are retained.
"""
import os
import struct
import zlib
import json


def prepare(handler, plan):
    loader = handler.mtk.daloader
    page = int(handler.mtk.config.pagesize)
    capacity = int(loader.daconfig.storage.flashsize)
    if page != 512 or capacity % page or capacity < 4 * page:
        raise ValueError('GPT refresh requires known 512-byte eMMC sectors')
    last = capacity // page - 1

    def read(lba, count):
        data = loader.readflash(lba * page, count * page, '', 'user', display=False)
        if not isinstance(data, (bytes, bytearray)) or len(data) != count * page:
            raise ValueError('GPT backup read incomplete at LBA %d' % lba)
        return bytes(data)

    primary = read(1, 1)
    if primary[:8] != b'EFI PART':
        raise ValueError('GPT refresh requires a valid primary GPT')
    revision, header_size, checksum, reserved = struct.unpack_from('<4I', primary, 8)
    if revision != 0x10000 or not 92 <= header_size <= page or reserved:
        raise ValueError('Unsupported GPT header format')
    header = bytearray(primary[:header_size])
    struct.pack_into('<I', header, 16, 0)
    if zlib.crc32(header) != checksum:
        raise ValueError('Primary GPT header CRC mismatch')
    current, alternate, first_usable, last_usable = struct.unpack_from('<4Q', primary, 24)
    entries_lba, count, entry_size, array_crc = struct.unpack_from('<Q3I', primary, 72)
    if current != 1 or alternate != last:
        raise ValueError('GPT capacity differs from live eMMC capacity')
    if not 1 <= count <= 4096 or entry_size < 128 or entry_size % 128 or count * entry_size > 4 * 1024 * 1024:
        raise ValueError('Unsupported GPT entry array size')
    array_length = count * entry_size
    sectors = (array_length + page - 1) // page
    backup_lba = last - sectors
    if not (2 <= entries_lba and entries_lba + sectors <= first_usable <= last_usable < backup_lba):
        raise ValueError('GPT metadata overlaps usable partitions')
    entries = read(entries_lba, sectors)
    if zlib.crc32(entries[:array_length]) != array_crc:
        raise ValueError('Primary GPT entry array CRC mismatch')
    # Preserve real-device evidence even when a later layout check refuses refresh.
    # These are diagnostic snapshots, not permission to repair unknown layouts.
    os.makedirs(plan['backup'], exist_ok=True)
    for name, data in [('gpt-observed-header.bin', primary), ('gpt-observed-entries.bin', entries)]:
        with open(os.path.join(plan['backup'], name), 'xb') as out:
            out.write(data)
            out.flush()
            os.fsync(out.fileno())
    print('PMK_GPT: Diagnostic snapshot saved at ' + plan['backup'], flush=True)
    print('PMK_GPT: Usable LBA 0x%x..0x%x; device last LBA 0x%x' %
          (first_usable, last_usable, last), flush=True)
    ranges = []
    for index in range(count):
        offset = index * entry_size
        if entries[offset:offset + 16] == b'\0' * 16:
            continue
        start, end = struct.unpack_from('<2Q', entries, offset + 32)
        name = entries[offset + 56:offset + 128].decode('utf-16-le', errors='replace').split('\0', 1)[0]
        # Validate against the regions this refresh actually writes (primary header,
        # primary entries, secondary entries+header) rather than the advisory
        # first_usable/last_usable window: stock MTK GPTs legitimately place entries
        # (e.g. boot_para at LBA 0x40) below a first_usable set far higher.
        if not (entries_lba + sectors) <= start <= end < backup_lba:
            raise ValueError('GPT partition overlaps GPT metadata: entry %d (%s), LBA 0x%x..0x%x; '
                             'entries occupy LBA 0x%x..0x%x and backup starts at 0x%x. '
                             'Nothing written. Disable GPT refresh to retain the current table.' %
                             (index + 1, name or 'unnamed', start, end,
                              entries_lba, entries_lba + sectors - 1, backup_lba))
        ranges.append((start, end, name))
    ranges.sort()
    if not ranges:
        raise ValueError('GPT has no active partitions')
    for left, right in zip(ranges, ranges[1:]):
        if left[1] >= right[0]:
            raise ValueError('GPT partitions overlap: %s ends at 0x%x; %s starts at 0x%x' %
                             (left[2], left[1], right[2], right[0]))

    # Back up all bytes that will be overwritten before allowing any device write.
    old_backup = read(backup_lba, sectors + 1)
    os.makedirs(plan['backup'], exist_ok=True)
    for name, data in [('gpt-primary-header.bin', primary), ('gpt-primary-entries.bin', entries),
                       ('gpt-secondary.bin', old_backup)]:
        with open(os.path.join(plan['backup'], name), 'xb') as out:
            out.write(data)
            out.flush()
            os.fsync(out.fileno())
    with open(os.path.join(plan['backup'], 'gpt-layout.json'), 'x', encoding='utf-8') as out:
        json.dump({'sector_size': page, 'capacity': capacity,
                   'primary_header_lba': 1, 'primary_entries_lba': entries_lba,
                   'secondary_backup_lba': backup_lba, 'entry_array_sectors': sectors}, out, indent=2)
        out.flush()
        os.fsync(out.fileno())
    backup = bytearray(primary)
    struct.pack_into('<2Q', backup, 24, last, 1)
    struct.pack_into('<Q', backup, 72, backup_lba)
    struct.pack_into('<I', backup, 16, 0)
    struct.pack_into('<I', backup, 16, zlib.crc32(backup[:header_size]))
    print('PMK_GPT: Backup saved at %s; all partition sizes retained' % plan['backup'], flush=True)
    # Secondary first; retain a valid primary if the secondary write fails.
    return [('Secondary', backup_lba, entries + bytes(backup)),
            ('Primary entries', entries_lba, entries), ('Primary header', 1, primary)]


def refresh(handler, targets, cancelled):
    loader = handler.mtk.daloader
    for label, lba, data in targets:
        cancelled()
        print('PMK_GPT: Refreshing ' + label, flush=True)
        if not loader.writeflash(addr=lba * 512, length=len(data), filename='',
                                 parttype='user', wdata=data, display=False):
            raise ValueError('GPT write failed: ' + label)
        actual = loader.readflash(lba * 512, len(data), '', 'user', display=False)
        if actual != data:
            raise ValueError('GPT readback mismatch: ' + label)
        print('PMK_GPT: ' + label + ' readback verified', flush=True)
