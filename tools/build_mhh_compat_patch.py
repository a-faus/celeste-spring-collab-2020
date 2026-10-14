#!/usr/bin/env python3
"""Package an upstream MaxHelpingHand source tree for the pinned browser Everest."""

import argparse
import gzip
import hashlib
import io
import re
import tarfile
import zipfile
from pathlib import Path, PurePosixPath


def add_tar_file(tar, name, data):
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts or "\\" in name:
        raise ValueError(f"Unsafe archive path: {name}")
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mtime = 0
    info.mode = 0o644
    info.uid = info.gid = 0
    info.uname = info.gname = ""
    tar.addfile(info, io.BytesIO(data))


def build(source_dir, assembly, output):
    source_dir = source_dir.resolve()
    assembly = assembly.resolve()
    output = output.resolve()
    metadata_path = source_dir / "everest.yaml"
    license_path = source_dir / "LICENSE"
    if not metadata_path.is_file() or not license_path.is_file() or not assembly.is_file():
        raise FileNotFoundError("The upstream source, license, and built compatible DLL are all required")

    yaml = metadata_path.read_text(encoding="utf-8-sig")
    if not re.search(r"(?m)^\s*- Name:\s*MaxHelpingHand\s*$", yaml):
        raise ValueError("The selected source is not MaxHelpingHand")
    if not re.search(r"(?m)^\s*Version:\s*1\.38\.0\s*$", yaml):
        raise ValueError("Expected the upstream MaxHelpingHand 1.38.0 source tree")
    if not re.search(r"(?m)^\s{6}Version:\s*1\.3847\.0\s*$", yaml):
        raise ValueError("Expected MaxHelpingHand 1.38.0's Everest 1.3847.0 requirement")
    # The browser bundle intentionally omits many optional integration mods.
    # Remove these hints so Everest does not keep postponing MHH while waiting
    # for optional mods that this edition deliberately does not ship.
    filtered_lines = []
    in_optional_dependencies = False
    for line in yaml.splitlines(keepends=True):
        if re.match(r"^\s{2}OptionalDependencies:\s*$", line):
            in_optional_dependencies = True
            continue
        if in_optional_dependencies:
            if not line.strip() or re.match(r"^\s{4,}", line):
                continue
            in_optional_dependencies = False
        filtered_lines.append(line)
    metadata = "".join(filtered_lines).encode("utf-8")
    if assembly.stat().st_size < 100_000:
        raise ValueError("The compatible MaxHelpingHand assembly is unexpectedly small")

    mod_buffer = io.BytesIO()
    with zipfile.ZipFile(mod_buffer, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as mod:
        def write(name, data):
            entry = zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o644 << 16
            mod.writestr(entry, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)

        write("everest.yaml", metadata)
        write("LICENSE", license_path.read_bytes())
        write("bin/Release/net452/MaxHelpingHand.dll", assembly.read_bytes())
        for dirname in ("Ahorn", "Dialog", "Graphics", "Loenn"):
            folder = source_dir / dirname
            if not folder.is_dir():
                raise FileNotFoundError(folder)
            for path in sorted(folder.rglob("*")):
                if path.is_file():
                    write(path.relative_to(source_dir).as_posix(), path.read_bytes())

    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("wb") as raw:
        with gzip.GzipFile(filename="", fileobj=raw, mode="wb", compresslevel=9, mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.USTAR_FORMAT) as tar:
                add_tar_file(tar, "Mods/MaxHelpingHand.zip", mod_buffer.getvalue())

    data = output.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    print(f"Created {output} ({len(data):,} bytes, sha256 {digest})")
    print("Overlay contains only Mods/MaxHelpingHand.zip; no saves, logs, or debug symbols.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_dir", type=Path, help="clean upstream 1.38.0 source checkout")
    parser.add_argument("compatible_dll", type=Path, help="built and browser-compatible MaxHelpingHand.dll")
    parser.add_argument("output", type=Path, help="output gzip-compressed tar patch")
    args = parser.parse_args()
    build(args.source_dir, args.compatible_dll, args.output)


if __name__ == "__main__":
    main()
