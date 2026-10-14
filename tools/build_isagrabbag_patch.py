#!/usr/bin/env python3
"""Build a tiny overlay that lets Spring Collab load without optional BingoUI."""

import argparse
import gzip
import hashlib
import io
import tarfile
import zipfile
from pathlib import Path, PurePosixPath


def safe_add(tar, name, data):
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


def patch_archive(bundle_path, output_path):
    with zipfile.ZipFile(bundle_path) as bundle:
        entry = next((item for item in bundle.infolist()
                      if PurePosixPath(item.filename).stem.lower() == "isagrabbag"), None)
        if entry is None:
            raise ValueError("The Spring Collab bundle does not contain IsaGrabBag.zip")
        source_bytes = bundle.read(entry)

    mod_buffer = io.BytesIO()
    removed_blocks = 0
    with zipfile.ZipFile(io.BytesIO(source_bytes)) as source, zipfile.ZipFile(mod_buffer, "w") as patched:
        metadata_name = next((name for name in source.namelist()
                              if name.lower().endswith("everest.yaml")), None)
        if metadata_name is None:
            raise ValueError("IsaGrabBag.zip is missing everest.yaml")
        for item in source.infolist():
            data = source.read(item.filename)
            if item.filename == metadata_name:
                text = data.decode("utf-8-sig")
                if "- Name: IsaGrabBag" not in text or "- Name: BingoUI" not in text:
                    raise ValueError("Unexpected IsaGrabBag package metadata")
                filtered = []
                in_optional_dependencies = False
                for line in text.splitlines(keepends=True):
                    if line.strip() == "OptionalDependencies:":
                        in_optional_dependencies = True
                        removed_blocks += 1
                        continue
                    if in_optional_dependencies:
                        if not line.strip() or line.startswith("    "):
                            continue
                        in_optional_dependencies = False
                    filtered.append(line)
                if removed_blocks != 1:
                    raise ValueError("Expected exactly one optional dependency block in IsaGrabBag")
                data = "".join(filtered).encode("utf-8")
                if "BingoUI" in data.decode("utf-8"):
                    raise ValueError("BingoUI optional dependency was not removed")
            patched.writestr(item, data)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("wb") as raw:
        with gzip.GzipFile(filename="", fileobj=raw, mode="wb", compresslevel=9, mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.USTAR_FORMAT) as tar:
                safe_add(tar, "Mods/IsaGrabBag.zip", mod_buffer.getvalue())
    data = output_path.read_bytes()
    print(f"Created {output_path} ({len(data):,} bytes, sha256 {hashlib.sha256(data).hexdigest()})")
    print("Overlay contains only Mods/IsaGrabBag.zip with its missing optional integration removed.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path, help="Spring Collab 2020 dependency bundle")
    parser.add_argument("output", type=Path, help="output gzip-compressed tar patch")
    args = parser.parse_args()
    patch_archive(args.bundle, args.output)


if __name__ == "__main__":
    main()
