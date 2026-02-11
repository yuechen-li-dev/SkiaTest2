#nullable enable

using SkiaSharp;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.Sprites;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

public sealed class SkiaStrideSilkProbe : SyncScript
{
    private const int W = 512;
    private const int H = 256;

    private Texture? _texture;
    private SpriteComponent? _spriteComponent;

    // --- CPU readback buffer (used in both modes) ---
    private SKBitmap? _cpuReadbackBitmap;
    private byte[]? _pixelBytes;

    // --- GPU Skia (Vulkan) ---
    private GRContext? _grContext;
    private SKSurface? _gpuSurface;     // GPU render target allocated by Skia
    private SKCanvas? _canvas;          // points at GPU surface if available; else CPU bitmap canvas
    private bool _usingGpuSkia;

    // --- CPU Skia fallback ---
    private SKBitmap? _cpuBitmap;
    private SKCanvas? _cpuCanvas;

    private SKPaint? _paint;
    private SKFont? _font;

    private float _t;

    // Vulkan loader exports
    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern IntPtr vkGetInstanceProcAddr(IntPtr instance, string pName);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern IntPtr vkGetDeviceProcAddr(IntPtr device, string pName);

    public override void Start()
    {
        Log.Info("=== SkiaStrideSilkProbe starting ===");
        Log.Info($"Graphics platform: {GraphicsDevice.Platform}");
        Log.Info($"Renderer: {GraphicsDevice.Adapter?.Description}");

        if (GraphicsDevice.Platform == GraphicsPlatform.Vulkan)
        {
            DumpMembersNoGet(GraphicsDevice, "GraphicsDevice");
        }

        // Create Stride texture + sprite display (unchanged)
        _texture = Texture.New2D(
            GraphicsDevice, W, H,
            PixelFormat.B8G8R8A8_UNorm,
            TextureFlags.ShaderResource,
            arraySize: 1,
            usage: GraphicsResourceUsage.Default);

        var spriteFromTexture = new SpriteFromTexture { Texture = _texture, PixelsPerUnit = 100f };
        _spriteComponent = Entity.Get<SpriteComponent>() ?? new SpriteComponent();
        if (Entity.Get<SpriteComponent>() == null) Entity.Add(_spriteComponent);
        _spriteComponent.SpriteProvider = spriteFromTexture;
        _spriteComponent.PremultipliedAlpha = true;

        _paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        _font = new SKFont(SKTypeface.Default, 28);

        _pixelBytes = new byte[W * H * 4];
        _cpuReadbackBitmap = new SKBitmap(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));

        // Try GPU Skia only if Stride is running Vulkan
        if (GraphicsDevice.Platform == GraphicsPlatform.Vulkan)
        {
            _usingGpuSkia = TryInitSkiaVulkanGpu();
        }

        if (!_usingGpuSkia)
        {
            InitCpuSkiaFallback();
        }

        DrawAndUpload();
    }

    public override void Update()
    {
        _t += (float)Game.UpdateTime.Elapsed.TotalSeconds;
        DrawAndUpload();
    }

    public override void Cancel()
    {
        // clean up
        _gpuSurface?.Dispose();
        _grContext?.Dispose();
        _cpuCanvas?.Dispose();
        _cpuBitmap?.Dispose();
        _cpuReadbackBitmap?.Dispose();
        _paint?.Dispose();
        _font?.Dispose();
    }

    private void InitCpuSkiaFallback()
    {
        Log.Warning("Skia GPU init failed (or not Vulkan). Falling back to CPU Skia.");

        var info = new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
        _cpuBitmap = new SKBitmap(info);
        _cpuCanvas = new SKCanvas(_cpuBitmap);
        _canvas = _cpuCanvas;
    }

    private bool TryInitSkiaVulkanGpu()
    {
        try { NativeLibrary.Load("vulkan-1"); Log.Info("Vulkan loader OK (vulkan-1.dll)"); }
        catch (Exception ex) { Log.Error("No Vulkan loader: " + ex); return false; }

        object gd = GraphicsDevice;

        // These exist per your dump:
        VkInstance instance;
        VkPhysicalDevice phys;
        VkDevice device;
        VkQueue queue;

        try
        {
            instance = GetProp<VkInstance>(gd, "NativeInstance");              // property
            phys = GetProp<VkPhysicalDevice>(gd, "NativePhysicalDevice");  // property
            device = GetField<VkDevice>(gd, "nativeDevice");                 // field
            queue = GetField<VkQueue>(gd, "NativeCommandQueue");            // field
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read Vulkan members from GraphicsDevice: " + ex);
            return false;
        }

        if (!TryFindQueueFamilyIndex(gd, out var qfi))
        {
            // We'll still try 0 as a last resort, but it's better if we find it.
            Log.Warning("Could not find QueueFamilyIndex via field-scan; defaulting to 0 (may fail).");
            qfi = 0;
        }

        // Convert Vortice handles to IntPtr for SkiaSharp
        IntPtr vkInstance = new IntPtr(unchecked((long)instance.Handle));
        IntPtr vkPhys = new IntPtr(unchecked((long)phys.Handle));
        IntPtr vkDevice = new IntPtr(unchecked((long)device.Handle));
        IntPtr vkQueue = new IntPtr(unchecked((long)queue.Handle));

        Log.Info("Stride Vulkan handles (targeted read):");
        Log.Info($"  VkInstance        = 0x{vkInstance.ToInt64():X}");
        Log.Info($"  VkPhysicalDevice  = 0x{vkPhys.ToInt64():X}");
        Log.Info($"  VkDevice          = 0x{vkDevice.ToInt64():X}");
        Log.Info($"  VkQueue           = 0x{vkQueue.ToInt64():X}");
        Log.Info($"  QueueFamilyIndex  = {qfi}");

        IntPtr GetProc(string name, IntPtr inst, IntPtr dev)
        {
            if (dev != IntPtr.Zero)
            {
                var p = vkGetDeviceProcAddr(dev, name);
                if (p != IntPtr.Zero) return p;
            }
            return vkGetInstanceProcAddr(inst, name);
        }

        var backend = new GRVkBackendContext
        {
            VkInstance = vkInstance,
            VkPhysicalDevice = vkPhys,
            VkDevice = vkDevice,
            VkQueue = vkQueue,
            GraphicsQueueIndex = qfi,
            GetProcedureAddress = GetProc,
            ProtectedContext = false,
        };

        _grContext = GRContext.CreateVulkan(backend);
        if (_grContext == null)
        {
            Log.Error("GRContext.CreateVulkan returned null.");
            return false;
        }

        Log.Info($"Skia GRContext backend: {_grContext.Backend}");

        var info = new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
        _gpuSurface = SKSurface.Create(_grContext, false, info);
        if (_gpuSurface == null)
        {
            Log.Error("SKSurface.Create(GRContext, ...) returned null (GPU surface create failed).");
            return false;
        }

        _canvas = _gpuSurface.Canvas;
        Log.Info("Skia GPU surface created successfully (Vulkan).");
        return true;
    }


    private void DrawAndUpload()
    {
        if (_texture == null || _canvas == null || _paint == null || _font == null || _pixelBytes == null || _cpuReadbackBitmap == null)
            return;

        _canvas.Clear(new SKColor(20, 20, 26, 255));

        float bar = (float)(0.5 + 0.5 * Math.Sin(_t * 2.0));
        int barW = (int)(W * bar);

        using (var barPaint = new SKPaint { Color = new SKColor(80, 140, 255, 220), IsAntialias = true })
        {
            _canvas.DrawRoundRect(new SKRect(20, 40, 20 + barW, 80), 12, 12, barPaint);
        }

        _paint.Color = SKColors.White;
        _canvas.DrawText(_usingGpuSkia ? "Skia GPU (Vulkan) -> Readback -> Stride Texture" : "Skia CPU -> Stride Texture", 20, 130, _font, _paint);
        _canvas.DrawText($"t={_t:0.00}", 20, 165, _font, _paint);

        _paint.Color = new SKColor(255, 200, 80, 255);
        _canvas.DrawText(_usingGpuSkia ? "If this moves, GPU Skia is live." : "CPU mode", 20, 210, _font, _paint);

        if (_usingGpuSkia && _grContext != null)
        {
            _canvas.Flush();
            _grContext.Flush();
        }

        // Read back into CPU bitmap (works for both CPU surface and GPU surface)
        bool ok;
        if (_usingGpuSkia && _gpuSurface != null)
        {
            var rb = _cpuReadbackBitmap;
            if (rb == null) return;

            var info = rb.Info;
            var ptr = rb.GetPixels();
            if (ptr == IntPtr.Zero) return;

            // rowBytes must match the destination buffer stride
            ok = _gpuSurface.ReadPixels(info, (nint)ptr, info.RowBytes, srcX: 0, srcY: 0);
        }
        else
        {
            // CPU path: just copy from CPU bitmap pixels
            ok = true;
        }

        if (!ok)
        {
            Log.Error("ReadPixels failed.");
            return;
        }

        // Copy BGRA bytes out
        IntPtr src = (_usingGpuSkia ? _cpuReadbackBitmap : _cpuBitmap)?.GetPixels() ?? IntPtr.Zero;
        if (src == IntPtr.Zero) return;

        Marshal.Copy(src, _pixelBytes, 0, _pixelBytes.Length);

        // Upload to Stride texture
        _texture.SetData(Game.GraphicsContext.CommandList, _pixelBytes);
    }

    // 1) Safe-ish: read only a named property from GraphicsDevice (no crawling)
    private static T GetProp<T>(object obj, string propName)
    {
        var p = obj.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p == null) throw new MissingMemberException(obj.GetType().FullName, propName);
        return (T)p.GetValue(obj)!;
    }

    // 2) Safe: read a named field from GraphicsDevice (no crawling)
    private static T GetField<T>(object obj, string fieldName)
    {
        var f = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f == null) throw new MissingMemberException(obj.GetType().FullName, fieldName);
        return (T)f.GetValue(obj)!;
    }

    // 3) Find queue-family index by scanning *fields only* (and including primitive fields)
    private static bool TryFindQueueFamilyIndex(object root, out uint qfi)
    {
        qfi = 0;

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var q = new Queue<object>();
        q.Enqueue(root);

        int steps = 0;
        const int MaxSteps = 3000;

        while (q.Count > 0 && steps++ < MaxSteps)
        {
            var obj = q.Dequeue();
            if (obj == null) continue;
            if (!visited.Add(obj)) continue;

            var t = obj.GetType();
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? val = null;
                try { val = f.GetValue(obj); } catch { continue; }
                if (val == null) continue;

                // Check primitive fields for queue-family-ish names
                if (val is int i && IsQueueFamilyName(f.Name)) { qfi = unchecked((uint)i); return true; }
                if (val is uint u && IsQueueFamilyName(f.Name)) { qfi = u; return true; }

                // Traverse only Stride/Vortice objects
                var ft = f.FieldType;
                if (ft.IsPrimitive || ft == typeof(string)) continue;

                var full = ft.FullName?.ToLowerInvariant() ?? "";
                if (full.StartsWith("stride.") || full.StartsWith("vortice.vulkan"))
                    q.Enqueue(val);
            }
        }

        return false;
    }

    private static bool IsQueueFamilyName(string n)
    {
        n = n.ToLowerInvariant();
        return n.Contains("queuefamily")
            || n.Contains("queue_family")
            || (n.Contains("graphics") && n.Contains("queue") && n.Contains("index"))
            || (n.Contains("queue") && n.Contains("family") && n.Contains("index"));
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private void DumpMembersNoGet(object obj, string label)
    {
        var t = obj.GetType();
        Log.Info($"=== {label}: {t.FullName} ===");

        // Fields (safe to list)
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Log.Info($"F {f.Name} : {f.FieldType.FullName}");
        }

        // Properties (safe to list; DO NOT call GetValue)
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            Log.Info($"P {p.Name} : {p.PropertyType.FullName}");
        }

        Log.Info($"=== end {label} ===");
    }



}
