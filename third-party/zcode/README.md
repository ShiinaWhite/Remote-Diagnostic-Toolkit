# ZCode redistribution materials

Unmodified upstream source: https://github.com/zai-org/zcode

Commit: `3c5b227649371507ddc4eb46ac5133afe6db1cab` (CLI 0.17.0).

`LICENSE`, `NOTICE.md`, `THIRD-PARTY-NOTICES.md`, `inventory.json` and
`native-search-sources.json` are byte-exact copies of that checkout. The NOTICE
describes upstream behavior across distributions, including functionality RDT
does not expose. Inventory is an upstream conservative union, not an RDT SBOM.

The runtime and builtin config retain their upstream Apache-2.0 attribution.
The copied third-party notices retain their original applicable licenses.
This directory does not license RDT: **RDT LICENSE = UNDECIDED**.

RDT's launcher is separate code. The pinned upstream executable and builtin are
unmodified. A Windows distribution must include this directory with the runtime;
the SEA binary is staged locally and is not committed to this repository.
