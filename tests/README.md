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

- **D3D11 cubemap storage tests** - D3D11 doesn't support storage cubemaps.

- **Vulkan mapped-resource contract tests** - mapped update, mode, and binding
  cases remain exact known or backend-specific exclusions.

- **OpenGLES compute and buffer-range tests** - GLES on Windows desktop doesn't support compute shaders or buffer range binding. These are platform limitations, not bugs.

Qualification skip identities and reason codes are exact in
`eng/validation/test-result-policy.json`. A local device can expose additional
capability skips, such as a missing Vulkan validation layer, but the result
validator intentionally rejects those runs as qualification evidence.

### Vulkan allocation contracts

The Vulkan suite includes focused allocation regression tests for recording and bounded-submission hot paths. Run them after a Release build so debugger and first-build noise do not affect the measurement:

```bash
dotnet test tests/NeoVeldrid.Tests/NeoVeldrid.Tests.csproj -c Release --filter "FullyQualifiedName~VulkanRecordingPerformanceTests"
```

These tests warm the measured path before using `GC.GetAllocatedBytesForCurrentThread`; allocation-sensitive assertions apply only to managed allocations on the recording or submission thread.

### Visual smoke artifacts

The render tests validate GPU output by copying textures to staging resources and asserting decoded pixels. CI also runs the headless `ImageTint` sample through Vulkan, OpenGL, and OpenGL ES. It validates each backend's rendered pixels against the CPU tint calculation, reports each canonical PNG hash for diagnostics, then publishes every PNG as a directly viewable workflow artifact.

`ImageTint` exercises texture upload and sampling, uniform buffers, a graphics pipeline, repeated bounded command-list submissions with independent render targets and captures, staging readback, and image encoding. CI validates every bounded submission. The artifact complements the exact pixel tests with an image reviewers can inspect; it is not a replacement for those assertions or a broad golden-image suite.

### Continuous integration coverage

CI builds and runs the SPIR-V and non-GPU suites on Windows, Linux, and macOS. It also compiles the test project without Vulkan on all three platforms, without OpenGL on Windows and Linux, and without D3D11 on Windows.

The Linux job runs the complete Vulkan, OpenGL, and OpenGL ES test sets in isolated processes with Mesa software drivers under Xvfb, then publishes the cross-backend visual smoke images. The OpenGL test suite uses SDL's EGL path because Xvfb's GLX transport does not expose the sRGB-capable visual required by the swapchain regression tests; the OpenGL visual smoke invocation remains on the default GLX path so both transports are exercised. OpenGL ES also uses EGL because Xvfb does not provide the required GLX ES profile. Hosted Windows and macOS runners are build and non-GPU gates only because they do not provide a stable graphics device/display contract. Before a release or a backend-specific change, run the commands above on real Windows hardware and run Vulkan through MoltenVK on macOS. The macOS Vulkan suite is compiled in CI but is not currently executed there.

OpenGL ES reports block-compressed staging textures as unsupported because ES has no portable API for downloading raw compressed blocks. The compressed-array copy regression still verifies exact BC3 block data on ES 3.2 by reinterpreting each block as an integer texel through core `CopyImageSubData`, then using the ordinary uncompressed staging path.

### Vulkan debug callback note

Upstream's Vulkan debug callback throws a managed exception from an `[UnmanagedCallersOnly]` native callback, which is undefined behavior and crashes the test process. Our fix stores the error and throws from managed code after the Vulkan call returns. This allows all Vulkan tests to run to completion instead of aborting mid-suite.
