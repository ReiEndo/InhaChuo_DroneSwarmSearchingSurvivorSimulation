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

        analysisPython = pkgs.python3.withPackages (ps: with ps; [
          pandas
          numpy
          matplotlib
          seaborn
          scipy
        ]);

        stat-collect = pkgs.writeShellApplication {
          name = "stat-collect";
          runtimeInputs = [ analysisPython ];
          text = ''
            python analysis/collect_telemetry.py "$@"
          '';
        };

        stat-analyze = pkgs.writeShellApplication {
          name = "stat-analyze";
          runtimeInputs = [ analysisPython ];
          text = ''
            python analysis/analyze_telemetry.py "$@"
          '';
        };

        stat-dashboard = pkgs.writeShellApplication {
          name = "stat-dashboard";
          runtimeInputs = [ analysisPython ];
          text = ''
            python analysis/plot_three_run_dashboard.py "$@"
          '';
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

        dn-stage = pkgs.writeShellApplication {
          name = "dn-stage";
          runtimeInputs = with pkgs; [ coreutils ];
          text = ''
            build_dir="Native/drone-navigation-native/build/src"
            staged=0

            mkdir -p \
              Assets/Plugins/macOS \
              Assets/Plugins/Linux \
              Assets/Plugins/Windows/x86_64 \
              Assets/Plugins/WebGL

            if [ -f "$build_dir/libdrone_algo.dylib" ]; then
              cp "$build_dir/libdrone_algo.dylib" Assets/Plugins/macOS/libdrone_algo.dylib
              echo "Staged Assets/Plugins/macOS/libdrone_algo.dylib"
              staged=1
            fi

            if [ -f "$build_dir/libdrone_algo.so" ]; then
              cp "$build_dir/libdrone_algo.so" Assets/Plugins/Linux/libdrone_algo.so
              echo "Staged Assets/Plugins/Linux/libdrone_algo.so"
              staged=1
            fi

            if [ -f "$build_dir/drone_algo.dll" ]; then
              cp "$build_dir/drone_algo.dll" Assets/Plugins/Windows/x86_64/drone_algo.dll
              echo "Staged Assets/Plugins/Windows/x86_64/drone_algo.dll"
              staged=1
            elif [ -f "$build_dir/libdrone_algo.dll" ]; then
              cp "$build_dir/libdrone_algo.dll" Assets/Plugins/Windows/x86_64/drone_algo.dll
              echo "Staged Assets/Plugins/Windows/x86_64/drone_algo.dll"
              staged=1
            fi

            for web_artifact in \
              "$build_dir/libdrone_algo.a" \
              "$build_dir/drone_algo.a" \
              "$build_dir/libdrone_algo.bc" \
              "$build_dir/drone_algo.bc" \
              "$build_dir/libdrone_algo.wasm" \
              "$build_dir/drone_algo.wasm" \
              "$build_dir/libdrone_algo.js" \
              "$build_dir/drone_algo.js"
            do
              if [ -f "$web_artifact" ]; then
                cp "$web_artifact" "Assets/Plugins/WebGL/$(basename "$web_artifact")"
                echo "Staged Assets/Plugins/WebGL/$(basename "$web_artifact")"
                staged=1
              fi
            done

            if [ "$staged" -eq 0 ]; then
              echo "No native library was found in $build_dir. Run dn-build first." >&2
              exit 1
            fi
          '';
        };

        dn-unity-plugin = pkgs.writeShellApplication {
          name = "dn-unity-plugin";
          runtimeInputs = [
            dn-build
            dn-stage
          ];
          text = ''
            dn-build
            dn-stage
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
            dn-stage
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
            dn-stage
            dn-unity-plugin
            dn-format
            dn-lint
            dn-check
            stat-collect
            stat-analyze
            stat-dashboard
            cmake
            ninja
            clang-tools
            gdb
            analysisPython
            dotnet-sdk
            uv
          ];

          shellHook = ''
            echo "inhachuo2 dev shell ready"
          '';
        };
      }
    );
}
