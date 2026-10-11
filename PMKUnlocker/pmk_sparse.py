"""Android sparse-to-raw conversion with bounded memory and strict validation.

Format: AOSP libsparse/sparse_format.h. DONT_CARE bytes are explicitly zeroed.
"""
import os
import struct
import zlib
import time
from contextlib import nullcontext

MAGIC = 0xED26FF3A
BUFFER = 1024 * 1024


def exact(stream, size):
    data = stream.read(size)
    if len(data) != size:
        raise ValueError("Truncated sparse image")
    return data


def header(stream):
    prefix = stream.read(4)
    if prefix != struct.pack('<I', MAGIC):
        return None
    values = struct.unpack('<I4H4I', prefix + exact(stream, 24))
    _, major, minor, file_header, chunk_header, block, blocks, chunks, crc = values
    if major != 1 or file_header < 28 or chunk_header < 12 or block == 0 or block % 4 or blocks == 0:
        raise ValueError("Invalid sparse header")
    exact(stream, file_header - 28)
    return chunk_header, block, blocks, chunks, crc


def inspect(path, limit):
    """Validate chunk boundaries/counts without expanding or allocating the output."""
    with open(path, 'rb') as source:
        info = header(source)
        if info is None:
            size = os.fstat(source.fileno()).st_size
            if size <= 0 or size > limit:
                raise ValueError("Raw image is empty or exceeds partition")
            return False, size
        chunk_header, block, blocks, chunks, _ = info
        size = blocks * block
        if size > limit:
            raise ValueError("Expanded image exceeds partition")
        file_size = os.fstat(source.fileno()).st_size
        total = 0
        for _ in range(chunks):
            kind, _, count, length = struct.unpack('<2H2I', exact(source, 12))
            exact(source, chunk_header - 12)
            payload = {0xCAC1: count * block, 0xCAC2: 4, 0xCAC3: 0, 0xCAC4: 4}.get(kind)
            if payload is None or length != chunk_header + payload or (kind == 0xCAC4 and count != 0):
                raise ValueError("Invalid sparse chunk type or length")
            total += count
            if total > blocks or source.tell() + payload > file_size:
                raise ValueError("Sparse chunk exceeds declared image bounds")
            source.seek(payload, 1)
        if total != blocks or source.tell() != file_size:
            raise ValueError("Sparse block count or trailing data mismatch")
        return True, size


def expand(path, destination, limit, cancelled=lambda: None, progress=lambda done, total: None):
    sparse, size = inspect(path, limit)
    if not sparse:
        raise ValueError("Expected a sparse image")
    output = nullcontext(destination) if hasattr(destination, 'write') else open(destination, 'xb')
    with open(path, 'rb') as source, output as target:
        chunk_header, block, blocks, chunks, image_crc = header(source)
        crc = 0
        written = 0
        last_report = time.monotonic()
        progress(0, size)
        for _ in range(chunks):
            cancelled()
            kind, _, count, length = struct.unpack('<2H2I', exact(source, 12))
            exact(source, chunk_header - 12)
            remaining = count * block
            if kind == 0xCAC4:
                if struct.unpack('<I', exact(source, 4))[0] != crc:
                    raise ValueError("Sparse CRC32 chunk mismatch")
                continue
            pattern = exact(source, 4) if kind == 0xCAC2 else b'\0' * 4
            repeated = pattern * (BUFFER // 4) if kind != 0xCAC1 else None
            while remaining:
                cancelled()
                amount = min(BUFFER, remaining)
                data = exact(source, amount) if kind == 0xCAC1 else repeated[:amount]
                target.write(data)
                crc = zlib.crc32(data, crc)
                written += amount
                remaining -= amount
                if time.monotonic() - last_report >= 1:
                    progress(written, size)
                    last_report = time.monotonic()
        if written != size or (image_crc and crc != image_crc):
            raise ValueError("Expanded image length or CRC32 mismatch")
        target.flush()
        progress(written, size)
    return size


class SparseStream:
    """Sequential byte slices for XFlash wdata, without a temporary raw file.

    Call validate() before the first device write. DONT_CARE remains zero-filled
    to preserve existing tool semantics; it never exposes stale userdata.
    """
    def __init__(self, path, limit):
        self.path = path
        self.limit = limit
        sparse, self.size = inspect(path, limit)
        if not sparse or self.size % 512:
            raise ValueError('Streaming requires a sector-aligned sparse image')
        self.source = None
        self.position = 0
        self.remaining = 0
        self.kind = None
        self.pattern = None

    def validate(self, cancelled, progress):
        class Sink:
            def write(self, data):
                return len(data)
            def flush(self):
                pass
        expand(self.path, Sink(), self.limit, cancelled, progress)

    def __getitem__(self, request):
        if not isinstance(request, slice) or request.step not in (None, 1):
            raise ValueError('Unsupported sparse stream access')
        start, stop = request.start, request.stop
        if start != self.position or stop < start or stop > self.size:
            raise ValueError('Nonsequential sparse stream access')
        if self.source is None:
            self.source = open(self.path, 'rb')
            self.chunk_header, self.block, _, self.chunks, _ = header(self.source)
        output = bytearray()
        while self.position < stop:
            if not self.remaining:
                if not self.chunks:
                    raise ValueError('Sparse stream ended early')
                self.kind, _, count, _ = struct.unpack('<2H2I', exact(self.source, 12))
                exact(self.source, self.chunk_header - 12)
                self.chunks -= 1
                if self.kind == 0xCAC4:
                    exact(self.source, 4)
                    continue
                self.remaining = count * self.block
                if self.kind == 0xCAC2:
                    self.pattern = exact(self.source, 4)
                elif self.kind == 0xCAC3:
                    self.pattern = b'\0' * 4
                elif self.kind != 0xCAC1:
                    raise ValueError('Invalid sparse stream chunk')
                if not self.remaining:
                    continue
            amount = min(stop - self.position, self.remaining)
            if self.kind == 0xCAC1:
                output.extend(exact(self.source, amount))
            else:
                # XFlash packet lengths and sparse block sizes are multiples of 4.
                if amount % 4:
                    raise ValueError('Unaligned sparse fill request')
                output.extend(self.pattern * (amount // 4))
            self.remaining -= amount
            self.position += amount
        return output

    def close(self):
        if self.source is not None:
            self.source.close()
        self.source = None

    def rewind(self):
        self.close()
        self.position = 0
        self.remaining = 0
