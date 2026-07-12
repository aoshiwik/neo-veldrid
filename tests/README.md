# Tests

## SPIRV Tests (no GPU required)

Tests SPIR-V cross-compilation (HLSL, GLSL, ESSL, MSL), GLSL-to-SPIR-V compilation, shader reflection, and JSON serialization.

```bash
dotnet test tests/NeoVeldrid.SPIRV.Tests/NeoVeldrid.SPIRV.Tests.csproj -c Release
```

## GPU Tests (requires graphics hardware or a software renderer)

Tests buffers, textures, framebuffers, compute, rendering, pipelines, resource sets, and swapchains against real GPU backends. Backend selection is automatic based on the platform:

| Backend | Windows | Linux | macOS |
|---------|---------|-------|-------|
| D3D11 | Yes | No | No |
| Vulkan | Yes | Yes | Yes (MoltenVK) |
| OpenGL | Yes | Yes | No |
| OpenGLES | Yes | Yes | No |

The graphics tests are serialized by `xunit.runner.json`. Graphics-device and SDL window initialization use process-global state and are not safe to run concurrently. Run the commands below one after another; do not launch backend test commands from concurrent shells.

Run all backends for the current platform:

```bash
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release
```

Run a specific backend only:

```bash
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release --filter "Backend=D3D11"
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release --filter "Backend=Vulkan"
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release --filter "Backend=OpenGL"
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release --filter "Backend=OpenGLES"
```

Run multiple backends:

```bash
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release --filter "Backend=D3D11|Backend=Vulkan"
```

For an explicit full platform verification, first run the portable suites, then each supported GPU backend in sequence:

```bash
dotnet test tests/NeoVeldrid.SPIRV.Tests/NeoVeldrid.SPIRV.Tests.csproj -c Release
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release -p:ExcludeGPU=true
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release --filter "Backend=D3D11"
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release --filter "Backend=Vulkan"
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release --filter "Backend=OpenGL"
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release --filter "Backend=OpenGLES"
```

Only run commands for backends supported by the current platform. On macOS, the GPU command is the Vulkan command only.

Run a specific test across all backends:

```bash
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release --filter "Map_WrongFlags_Throws"
```

Run only non-GPU tests (for CI or machines without graphics hardware):

```bash
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release -p:ExcludeGPU=true
```

### Skipped tests

Some tests are skipped at runtime via `[SkippableFact]` + `Skip.If`/`Skip.IfNot`:

- **UseBlendFactor on Vulkan** - triggers a Vulkan image layout validation error. Same error crashes upstream's process. Our fix to the debug callback turns it into a catchable exception instead.

- **D3D11 cubemap storage tests** - D3D11 doesn't support storage cubemaps.

- **OpenGLES compute and buffer-range tests** - GLES on Windows desktop doesn't support compute shaders or buffer range binding. These are platform limitations, not bugs.

Skip counts are driver-dependent. The categories above are expected; investigate skips with any other reason before treating a run as verified.

### Vulkan allocation contracts

The Vulkan suite includes focused allocation regression tests for recording and bounded-submission hot paths. Run them after a Release build so debugger and first-build noise do not affect the measurement:

```bash
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release --filter "FullyQualifiedName~VulkanRecordingPerformanceTests"
```

These tests warm the measured path before using `GC.GetAllocatedBytesForCurrentThread`; allocation-sensitive assertions apply only to managed allocations on the recording or submission thread.

### Continuous integration coverage

CI builds and runs the SPIR-V and non-GPU suites on Windows, Linux, and macOS. It also compiles the test project without Vulkan on all three platforms, without OpenGL on Windows and Linux, and without D3D11 on Windows.

The Linux job runs the complete Vulkan, OpenGL, and OpenGL ES test set serially with Mesa software drivers under Xvfb. Hosted Windows and macOS runners are build and non-GPU gates only because they do not provide a stable graphics device/display contract. Before a release or a backend-specific change, run the commands above on real Windows hardware and run Vulkan through MoltenVK on macOS. The macOS Vulkan suite is compiled in CI but is not currently executed there.

### Vulkan debug callback note

Upstream's Vulkan debug callback throws a managed exception from an `[UnmanagedCallersOnly]` native callback, which is undefined behavior and crashes the test process. Our fix stores the error and throws from managed code after the Vulkan call returns. This allows all Vulkan tests to run to completion instead of aborting mid-suite.
