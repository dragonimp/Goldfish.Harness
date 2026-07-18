# SQLite Vec1 native extension

- Upstream: https://sqlite.org/vec1/
- Source: https://sqlite.org/vec1/raw/vec1.c?ci=tip
- Version: 0.7
- Source SHA-256: `8571bb4f77f9547d11ad11e2f72e0de7d3b2ab44e7930151998bce9377ed4b86`
- Binary SHA-256: `6cfc8621f540ddfb93719e570534f3f82f05ed7ab56209e3157d88892c051082`
- Target: macOS arm64
- Build: `cc -O3 -DNDEBUG -I/opt/homebrew/opt/sqlite/include -shared -fPIC vec1.c -o vec1.dylib`
- Runtime report: `version 0.7 (NEON, multi-threaded)`
- Signing: ad-hoc code signature for local macOS loading

The upstream source dedicates the code to the public domain using SQLite's
standard blessing.
