# TAW Vulkan performance fixes

This branch carries allocation and submission-lifetime fixes used by TAW's
interactive graphics systems. It is based on NeoVeldrid 1.2.0 commit
`989d2d1bb999489ad51405c15683fc9c4b61c5c3`.

## Changes

1. `VkCommandList.SetScissorRect` compares the four `Rect2D` scalar fields.
   Silk.NET's struct does not implement value equality, so the former
   `Equals(object)` path boxed and allocated in the draw loop.
2. The retained command-list submission dictionary uses a comparer keyed by
   the native command-buffer handle. The default comparer boxes Silk.NET's
   `CommandBuffer` struct during hashing and equality. Shared staging cleanup
   state is carried directly by the ordered fence record, avoiding three more
   dictionaries and making publication atomic with the fence.
3. `CommandListDescription` can bound the number of retained in-flight
   submissions and pre-size each submission's tracked-resource set. When the
   bound is reached, Vulkan waits for the command list's oldest submission
   fence instead of growing managed state or draining unrelated queue work.
4. Submission fences are retained in queue order, Vulkan status and wait
   results are checked, and completed staging records are recycled without
   holding their lock across a fence wait.
5. Vulkan result validation remains active in Release builds. Native API
   failures are no longer silently discarded by conditional call-site
   compilation.

The new description values default to zero, preserving adaptive behavior for
existing callers. TAW's interactive frame command list uses eight retained
submission records with space for 256 tracked resources per record.

## Compatibility

The project version is `1.2.1-taw.1`, while `AssemblyVersion` remains
`1.0.0.0` so the public NeoVeldrid 1.2.0 extension assemblies keep their CLR
reference compatibility.

## Validation

Before advancing the upstream base or changing the submission policy:

- build and run the NeoVeldrid non-GPU test suite;
- build TAW's `Domain.graphics` project;
- run the environment-authoring steady-state allocation benchmark; and
- remove a local fix when upstream contains an equivalent implementation.
