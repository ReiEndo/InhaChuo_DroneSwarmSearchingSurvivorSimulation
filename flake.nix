{
  description = "inhachuo2 development environment";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs =
    {
      self,
      nixpkgs,
      flake-utils,
    }:
    flake-utils.lib.eachDefaultSystem (
      system:
      let
        pkgs = import nixpkgs {
          inherit system;
        };

        dn-config = pkgs.writeShellApplication {
          name = "dn-config";
          runtimeInputs = with pkgs; [
            cmake
            ninja
          ];
          text = ''
            cmake -S Native/drone-navigation-native --preset dev
          '';
        };

        dn-config-tests = pkgs.writeShellApplication {
          name = "dn-config-tests";
          runtimeInputs = with pkgs; [
            cmake
            ninja
          ];
          text = ''
            cmake -S Native/drone-navigation-native --preset tests
          '';
        };

        dn-build = pkgs.writeShellApplication {
          name = "dn-build";
          runtimeInputs = with pkgs; [ cmake ];
          text = ''
            cmake --build Native/drone-navigation-native/build
          '';
        };

        dn-test = pkgs.writeShellApplication {
          name = "dn-test";
          runtimeInputs = with pkgs; [ cmake ];
          text = ''
            ctest --test-dir Native/drone-navigation-native/build --output-on-failure
          '';
        };

        dn-format = pkgs.writeShellApplication {
          name = "dn-format";
          runtimeInputs = with pkgs; [
            clang-tools
            findutils
          ];
          text = ''
            find Native/drone-navigation-native/src \
              -type f \( -name '*.cc' -o -name '*.h' \) \
              -print0 | xargs -0 clang-format -i
          '';
        };

        dn-lint = pkgs.writeShellApplication {
          name = "dn-lint";
          runtimeInputs = with pkgs; [
            clang-tools
            findutils
          ];
          text = ''
            if [ ! -f Native/drone-navigation-native/build/compile_commands.json ]; then
              echo "Missing compile_commands.json; running dn-config-tests first."
              dn-config-tests
            fi

            find Native/drone-navigation-native/src \
              -type f -name '*.cc' \
              -print0 | xargs -0 clang-tidy \
              -quiet \
              -p Native/drone-navigation-native/build \
              2> >(grep -v 'warnings generated\.$' >&2)
          '';
        };

        dn-check = pkgs.writeShellApplication {
          name = "dn-check";
          runtimeInputs = [
            dn-config-tests
            dn-build
            dn-test
            dn-lint
          ];
          text = ''
            dn-config-tests
            dn-build
            dn-test
            dn-lint
          '';
        };

        droneNavigationNative = pkgs.stdenv.mkDerivation {
          pname = "drone-navigation-native";
          version = "0.1.0";

          src = ./Native/drone-navigation-native;

          nativeBuildInputs = with pkgs; [
            cmake
            ninja
          ];

          cmakeFlags = [
            "-DENABLE_TESTING=OFF"
            "-DENABLE_INSTALL=ON"
            "-DCMAKE_BUILD_TYPE=Release"
          ];

          meta = with pkgs.lib; {
            description = "Native drone navigation and path-planning library for inhachuo2";
            platforms = platforms.linux ++ platforms.darwin;
          };
        };
      in
      {
        packages.drone-navigation-native = droneNavigationNative;
        packages.default = droneNavigationNative;

        devShells.default = pkgs.mkShell {
          packages = with pkgs; [
            dn-config
            dn-config-tests
            dn-build
            dn-test
            dn-format
            dn-lint
            dn-check
            cmake
            ninja
            clang-tools
            gdb
            python3
            dotnet-sdk
          ];

          shellHook = ''
            echo "inhachuo2 dev shell ready"
          '';
        };
      }
    );
}
