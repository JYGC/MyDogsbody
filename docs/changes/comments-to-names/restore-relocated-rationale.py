"""Undo phase 10 of comments-to-names: put every relocated comment block back into the source.

Reads docs/changes/comments-to-names/rationale/<Project>.md, finds each one-line pointer

    <indent>/// Rationale: docs/changes/comments-to-names/rationale/<Project>.md - <File>.fs: <symbol>

in the .fs files under --root, and replaces it with the block the section holds, re-commented at the
pointer's own indentation, with the pointer's own comment style and line ending.

    python restore-relocated-rationale.py            # the repository this file lives in
    python restore-relocated-rationale.py --root DIR # another checkout (used to test the round trip)

The rationale folder is left in place; delete it afterwards if the blocks should not live there.
"""
import argparse
import os
import re
import sys

POINTER = re.compile(
    r"^(?P<indent>[ \t]*)(?P<style>///?) Rationale: docs/changes/comments-to-names/rationale/"
    r"(?P<project>[^ ]+)\.md - (?P<key>.+)$"
)


def sections_of(document_path):
    """{heading key: text lines} for every '### ' section that holds a fenced text block."""
    sections = {}
    key = None
    lines = open(document_path, encoding="utf-8").read().split("\n")
    index = 0
    while index < len(lines):
        line = lines[index]
        if line.startswith("### "):
            key = line[4:]
        opening = re.match(r"^(`{3,})text$", line)
        if opening and key is not None:
            fence = opening.group(1)
            index += 1
            text = []
            while index < len(lines) and lines[index] != fence:
                text.append(lines[index])
                index += 1
            sections[key] = text
            key = None
        index += 1
    return sections


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=os.path.abspath(os.path.join(here, "..", "..", "..")))
    root = parser.parse_args().root

    rationale = os.path.join(root, "docs", "changes", "comments-to-names", "rationale")
    documents = {}
    restored = 0
    missing = []

    for directory, directory_names, file_names in os.walk(root):
        directory_names[:] = [n for n in directory_names if n not in ("obj", "bin", ".git", "docs")]
        for file_name in file_names:
            if not file_name.endswith(".fs"):
                continue
            path = os.path.join(directory, file_name)
            raw = open(path, "rb").read().decode("utf-8")
            pieces = raw.splitlines(keepends=True)
            output = []
            changed = False
            for piece in pieces:
                body = piece.rstrip("\r\n")
                ending = piece[len(body):]
                match = POINTER.match(body)
                if not match:
                    output.append(piece)
                    continue
                project = match.group("project")
                if project not in documents:
                    documents[project] = sections_of(os.path.join(rationale, project + ".md"))
                text = documents[project].get(match.group("key"))
                if text is None:
                    missing.append((path, match.group("key")))
                    output.append(piece)
                    continue
                marker = match.group("indent") + match.group("style")
                for text_line in text:
                    output.append((marker + " " + text_line if text_line else marker) + ending)
                restored += 1
                changed = True
            if changed:
                open(path, "wb").write("".join(output).encode("utf-8"))

    print("blocks restored:", restored)
    if missing:
        print("pointers with no section:", missing)
        sys.exit(1)


main()
