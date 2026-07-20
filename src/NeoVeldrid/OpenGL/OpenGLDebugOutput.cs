using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace NeoVeldrid.OpenGL;

/// <summary>
/// Owns one OpenGL context's debug callback and the API spelling used to reach it.
/// </summary>
/// <remarks>
/// Desktop KHR_debug uses the unsuffixed entry points. OpenGL ES implementations
/// before ES 3.2 use the KHR-suffixed entry points instead. Keeping that distinction
/// here prevents extension discovery from being confused with a callable transport.
/// </remarks>
internal abstract unsafe class OpenGLDebugOutput
{
    internal const uint ProbeMessageId = 0x4E56444C; // "NVDL"
    internal const string ProbeMessageText = "NeoVeldrid OpenGL debug callback probe";

    private const uint GlDebugOutput = 0x92E0;
    private const uint GlDebugOutputSynchronous = 0x8242;
    private const uint GlDontCare = 0x1100;

    private readonly GL _gl;
    private readonly GraphicsDeviceValidation _validation;
    private DebugProc _callback;
    private GCHandle _callbackRoot;
    private bool _nativeCallbackMayBeRegistered;
    private bool _registered;

    protected OpenGLDebugOutput(GL gl, GraphicsDeviceValidation validation)
    {
        _gl = gl;
        _validation = validation;
    }

    internal abstract string Transport { get; }

    internal bool SynchronousDelivery { get; private set; }

    internal bool ProbePassed { get; private set; }

    /// <summary>
    /// Gets whether the managed callback is deliberately rooted because the native
    /// context may still hold its function pointer.
    /// </summary>
    internal bool IsCallbackRootRetained => _callbackRoot.IsAllocated;

    internal static OpenGLDebugOutput TryCreate(
        GL gl,
        Func<string, IntPtr> getProcAddress,
        OpenGLExtensions extensions,
        GraphicsBackend backend,
        GraphicsDeviceValidation validation)
    {
        if (extensions.GLVersion(4, 3) || extensions.GLESVersion(3, 2))
        {
            return new CoreDebugOutput(gl, validation, backend);
        }

        if (backend == GraphicsBackend.OpenGL && extensions.KHR_DebugExtension)
        {
            return new DesktopKhrDebugOutput(gl, validation);
        }

        if (backend == GraphicsBackend.OpenGLES && extensions.KHR_DebugExtension)
        {
            return GlesKhrDebugOutput.TryLoad(gl, getProcAddress, validation);
        }

        return null;
    }

    internal bool TryActivate(bool isDebugContext, out string inactiveReason)
    {
        if (!isDebugContext)
        {
            inactiveReason = "the current OpenGL context was not created with the debug flag";
            return false;
        }

        if (_nativeCallbackMayBeRegistered || _callbackRoot.IsAllocated)
        {
            inactiveReason = "the OpenGL debug callback is already registered or awaiting safe native release";
            return false;
        }

        try
        {
            _callback = OnDebugMessage;
            _callbackRoot = GCHandle.Alloc(_callback);
            // Registration APIs are void and may fail after changing native state. From
            // this point until a successful unregister or confirmed context destruction,
            // the callback must be treated as natively reachable.
            _nativeCallbackMayBeRegistered = true;
            RegisterCallback(_callback);
            _registered = true;

            _gl.Enable((EnableCap)GlDebugOutput);
            _gl.Enable((EnableCap)GlDebugOutputSynchronous);
            EnableAllMessages();
            SynchronousDelivery = _gl.IsEnabled((EnableCap)GlDebugOutputSynchronous);
            if (!SynchronousDelivery)
            {
                inactiveReason = "the driver did not enable synchronous OpenGL debug-message delivery";
                inactiveReason = AppendUnregisterFailure(inactiveReason, TryUnregister());
                return false;
            }

            long firstProbeSequence = _validation.NextSequence;
            InsertMessage(
                DebugSource.DebugSourceApplication,
                DebugType.DebugTypeMarker,
                ProbeMessageId,
                DebugSeverity.DebugSeverityNotification,
                ProbeMessageText);

            ProbePassed = _validation.HasMessageSince(
                firstProbeSequence,
                message =>
                    message.Source == nameof(DebugSource.DebugSourceApplication)
                    && message.Type == nameof(DebugType.DebugTypeMarker)
                    && message.Id == FormatId(ProbeMessageId)
                    && message.Text == ProbeMessageText);

            if (!ProbePassed)
            {
                inactiveReason = "the native OpenGL debug callback did not deliver its synchronous probe";
                inactiveReason = AppendUnregisterFailure(inactiveReason, TryUnregister());
                return false;
            }

            inactiveReason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            inactiveReason = $"OpenGL debug-output activation failed: {exception.GetType().Name}: {exception.Message}";
            inactiveReason = AppendUnregisterFailure(inactiveReason, TryUnregister());
            return false;
        }
    }

    internal void InsertTestMessage(DebugSeverity severity, uint id, string text)
    {
        if (!_registered)
        {
            throw new InvalidOperationException("OpenGL debug output is not active.");
        }

        InsertMessage(
            DebugSource.DebugSourceApplication,
            DebugType.DebugTypeOther,
            id,
            severity,
            text);
    }

    internal void Unregister()
    {
        if (!_nativeCallbackMayBeRegistered)
        {
            return;
        }

        UnregisterCallback();
        _nativeCallbackMayBeRegistered = false;
        _registered = false;
        ReleaseCallbackRoot();
    }

    /// <summary>
    /// Confirms that the native context which owned this callback has been destroyed.
    /// A native callback cannot execute after this boundary, so its managed root may
    /// be released even when explicit unregistration failed.
    /// </summary>
    internal void ConfirmContextDestroyed()
    {
        _nativeCallbackMayBeRegistered = false;
        _registered = false;
        ReleaseCallbackRoot();
    }

    internal static GraphicsDeviceValidationSeverity MapSeverity(uint severity) =>
        severity switch
        {
            (uint)DebugSeverity.DebugSeverityHigh => GraphicsDeviceValidationSeverity.Error,
            (uint)DebugSeverity.DebugSeverityMedium => GraphicsDeviceValidationSeverity.Warning,
            (uint)DebugSeverity.DebugSeverityLow => GraphicsDeviceValidationSeverity.Information,
            (uint)DebugSeverity.DebugSeverityNotification => GraphicsDeviceValidationSeverity.Verbose,
            _ => GraphicsDeviceValidationSeverity.Corruption,
        };

    internal static GraphicsDeviceValidationSeverity MapSeverity(uint type, uint severity)
    {
        if (type == (uint)DebugType.DebugTypeError
            || type == (uint)DebugType.DebugTypeUndefinedBehavior)
        {
            return GraphicsDeviceValidationSeverity.Error;
        }

        return MapSeverity(severity);
    }

    private void OnDebugMessage(
        GLEnum source,
        GLEnum type,
        int id,
        GLEnum severity,
        int length,
        IntPtr message,
        IntPtr userParam)
    {
        try
        {
            string text = message == IntPtr.Zero || length <= 0
                ? string.Empty
                : Marshal.PtrToStringUTF8(message, length) ?? string.Empty;
            uint unsignedId = unchecked((uint)id);
            _validation.Report(
                MapSeverity((uint)type, (uint)severity),
                FormatSource((uint)source),
                FormatType((uint)type),
                FormatId(unsignedId),
                text);
        }
        catch (Exception exception)
        {
            // Exceptions must never escape an unmanaged debug callback.
            try
            {
                _validation.Report(
                    GraphicsDeviceValidationSeverity.Corruption,
                    "OpenGLDebugCallback",
                    "CallbackFailure",
                    "managed-callback",
                    $"Failed to capture an OpenGL debug message: {exception.GetType().Name}: {exception.Message}");
            }
            catch
            {
                // There is no safe recovery path if even the diagnostic sink is unavailable.
            }
        }
    }

    private Exception TryUnregister()
    {
        try
        {
            Unregister();
            return null;
        }
        catch (Exception exception)
        {
            // Keep the GCHandle allocated. The native driver may still own the
            // callback pointer, so releasing it here would create a use-after-free.
            return exception;
        }
    }

    private static string AppendUnregisterFailure(string reason, Exception unregisterFailure) =>
        unregisterFailure == null
            ? reason
            : reason
                + $"; callback unregistration also failed: {unregisterFailure.GetType().Name}: "
                + unregisterFailure.Message;

    private void ReleaseCallbackRoot()
    {
        if (_callbackRoot.IsAllocated)
        {
            _callbackRoot.Free();
        }
        _callback = null;
    }

    private static string FormatSource(uint source) =>
        source switch
        {
            (uint)DebugSource.DebugSourceApi => nameof(DebugSource.DebugSourceApi),
            (uint)DebugSource.DebugSourceWindowSystem => nameof(DebugSource.DebugSourceWindowSystem),
            (uint)DebugSource.DebugSourceShaderCompiler => nameof(DebugSource.DebugSourceShaderCompiler),
            (uint)DebugSource.DebugSourceThirdParty => nameof(DebugSource.DebugSourceThirdParty),
            (uint)DebugSource.DebugSourceApplication => nameof(DebugSource.DebugSourceApplication),
            (uint)DebugSource.DebugSourceOther => nameof(DebugSource.DebugSourceOther),
            _ => $"0x{source:X8}",
        };

    private static string FormatType(uint type) =>
        type switch
        {
            (uint)DebugType.DebugTypeError => nameof(DebugType.DebugTypeError),
            (uint)DebugType.DebugTypeDeprecatedBehavior => nameof(DebugType.DebugTypeDeprecatedBehavior),
            (uint)DebugType.DebugTypeUndefinedBehavior => nameof(DebugType.DebugTypeUndefinedBehavior),
            (uint)DebugType.DebugTypePortability => nameof(DebugType.DebugTypePortability),
            (uint)DebugType.DebugTypePerformance => nameof(DebugType.DebugTypePerformance),
            (uint)DebugType.DebugTypeOther => nameof(DebugType.DebugTypeOther),
            (uint)DebugType.DebugTypeMarker => nameof(DebugType.DebugTypeMarker),
            (uint)DebugType.DebugTypePushGroup => nameof(DebugType.DebugTypePushGroup),
            (uint)DebugType.DebugTypePopGroup => nameof(DebugType.DebugTypePopGroup),
            _ => $"0x{type:X8}",
        };

    private static string FormatId(uint id) =>
        "0x" + id.ToString("X8", CultureInfo.InvariantCulture);

    protected abstract void RegisterCallback(DebugProc callback);

    protected abstract void UnregisterCallback();

    protected abstract void EnableAllMessages();

    protected abstract void InsertMessage(
        DebugSource source,
        DebugType type,
        uint id,
        DebugSeverity severity,
        string text);

    private class CoreDebugOutput : OpenGLDebugOutput
    {
        private readonly GL _coreGl;
        private readonly GraphicsBackend _backend;

        internal CoreDebugOutput(GL gl, GraphicsDeviceValidation validation, GraphicsBackend backend)
            : base(gl, validation)
        {
            _coreGl = gl;
            _backend = backend;
        }

        internal override string Transport => _backend == GraphicsBackend.OpenGLES
            ? "OpenGL ES 3.2 core debug output"
            : "OpenGL 4.3 core debug output";

        protected override void RegisterCallback(DebugProc callback) =>
            _coreGl.DebugMessageCallback(callback, null);

        protected override void UnregisterCallback() =>
            _coreGl.DebugMessageCallback(null, null);

        protected override void EnableAllMessages() =>
            _coreGl.DebugMessageControl(
                (GLEnum)GlDontCare,
                (GLEnum)GlDontCare,
                (GLEnum)GlDontCare,
                0,
                null,
                true);

        protected override void InsertMessage(
            DebugSource source,
            DebugType type,
            uint id,
            DebugSeverity severity,
            string text)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
            fixed (byte* message = utf8)
            {
                _coreGl.DebugMessageInsert(source, type, id, severity, (uint)utf8.Length, message);
            }
        }
    }

    private sealed class DesktopKhrDebugOutput : CoreDebugOutput
    {
        internal DesktopKhrDebugOutput(GL gl, GraphicsDeviceValidation validation)
            : base(gl, validation, GraphicsBackend.OpenGL)
        {
        }

        internal override string Transport => "GL_KHR_debug";
    }

    private sealed class GlesKhrDebugOutput : OpenGLDebugOutput
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void DebugMessageCallbackKhr(IntPtr callback, IntPtr userParam);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private unsafe delegate void DebugMessageInsertKhr(
            uint source,
            uint type,
            uint id,
            uint severity,
            int length,
            byte* message);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private unsafe delegate void DebugMessageControlKhr(
            uint source,
            uint type,
            uint severity,
            int count,
            uint* ids,
            byte enabled);

        private readonly DebugMessageCallbackKhr _registerCallback;
        private readonly DebugMessageInsertKhr _insertMessage;
        private readonly DebugMessageControlKhr _controlMessages;

        private GlesKhrDebugOutput(
            GL gl,
            GraphicsDeviceValidation validation,
            DebugMessageCallbackKhr registerCallback,
            DebugMessageInsertKhr insertMessage,
            DebugMessageControlKhr controlMessages)
            : base(gl, validation)
        {
            _registerCallback = registerCallback;
            _insertMessage = insertMessage;
            _controlMessages = controlMessages;
        }

        internal override string Transport => "GL_KHR_debug (OpenGL ES KHR entry points)";

        internal static GlesKhrDebugOutput TryLoad(
            GL gl,
            Func<string, IntPtr> getProcAddress,
            GraphicsDeviceValidation validation)
        {
            IntPtr callback = getProcAddress("glDebugMessageCallbackKHR");
            IntPtr insert = getProcAddress("glDebugMessageInsertKHR");
            IntPtr control = getProcAddress("glDebugMessageControlKHR");
            if (callback == IntPtr.Zero || insert == IntPtr.Zero || control == IntPtr.Zero)
            {
                return null;
            }

            return new GlesKhrDebugOutput(
                gl,
                validation,
                Marshal.GetDelegateForFunctionPointer<DebugMessageCallbackKhr>(callback),
                Marshal.GetDelegateForFunctionPointer<DebugMessageInsertKhr>(insert),
                Marshal.GetDelegateForFunctionPointer<DebugMessageControlKhr>(control));
        }

        protected override void RegisterCallback(DebugProc callback) =>
            _registerCallback(Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero);

        protected override void UnregisterCallback() =>
            _registerCallback(IntPtr.Zero, IntPtr.Zero);

        protected override void EnableAllMessages() =>
            _controlMessages(GlDontCare, GlDontCare, GlDontCare, 0, null, 1);

        protected override void InsertMessage(
            DebugSource source,
            DebugType type,
            uint id,
            DebugSeverity severity,
            string text)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
            fixed (byte* message = utf8)
            {
                _insertMessage((uint)source, (uint)type, id, (uint)severity, utf8.Length, message);
            }
        }
    }
}
