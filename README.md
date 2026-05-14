# inhachuo2

## Native Algorithm

```txt
Native/
```

Builds a shared C++ library and includes GoogleTest-based native tests.

### Quick start

Enter the Nix development shell:

```bash
nix develop
```

Native shortcuts:

```bash
dn-config        # configure native build, tests off
dn-config-tests  # configure native build, tests on + compile_commands.json
dn-build         # build native library/tests
dn-test          # run native tests
dn-format        # format C++ sources with clang-format
dn-lint          # lint C++ sources with clang-tidy
dn-check         # configure tests, build, test, then lint
```

Recommended first run:

```bash
dn-check
```

### Editor setup

For C/C++ editor diagnostics and autocomplete, configure tests once:

```bash
dn-config-tests
```

This generates:

```txt
Native/drone-navigation-native/build/compile_commands.json
```

Point your editor or `clangd` integration at that file if it is not detected automatically.
