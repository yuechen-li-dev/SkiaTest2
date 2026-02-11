#nullable enable

using SkiaSharp;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.Sprites;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

public sealed class SkiaStrideSilkProbeCPU : SyncScript
{
    // Keep it small so CPU->GPU upload cost is obvious but not insane
    private const int W = 512;
    private const int H = 256;

    private Texture? _texture;
    private SpriteComponent? _spriteComponent;

    private SKBitmap? _bitmap;
    private SKCanvas? _canvas;
    private SKPaint? _paint;
    private SKFont? _font;

    private byte[]? _pixelBytes; // B,G,R,A byte order (we’ll request BGRA from Skia)

    private float _t;

    public override void Start()
    {
        Log.Info("=== SkiaStrideSilkProbe starting  ===");

        Log.Info($"Graphics platform: {GraphicsDevice.Platform}");
        Log.Info($"Renderer: {GraphicsDevice.Adapter?.Description}");
        var rndAsm = typeof(Stride.Rendering.QueryManager).Assembly;

        var gfxAsm = typeof(GraphicsDevice).Assembly;
        Log.Info($"Stride.Graphics path: {gfxAsm.Location}");
        Log.Info($"Stride.Graphics version: {gfxAsm.GetName().Version}");
        Log.Info($"Stride.Rendering path: {rndAsm.Location}");
        Log.Info($"Stride.Rendering version: {rndAsm.GetName().Version}");

        DumpVulkanCandidates(GraphicsDevice, "GraphicsDevice");

        try
        {
            NativeLibrary.Load("vulkan-1");
            Log.Info("Vulkan loader OK (vulkan-1.dll)");
        }
        catch (Exception ex)
        {
            Log.Warning("Vulkan loader missing: " + ex);
        }


        // 1) Probe loaded assemblies: are we seeing Silk.NET and not SharpDX?
        DumpAssemblyProbe();

        // 2) Create a Stride texture we can upload into.
        _texture = Texture.New2D(
            GraphicsDevice,
            W,
            H,
            PixelFormat.B8G8R8A8_UNorm,
            TextureFlags.ShaderResource,
            arraySize: 1,
            usage: GraphicsResourceUsage.Default);

        // 3) Create a Sprite provider that uses our runtime texture.
        var spriteFromTexture = new SpriteFromTexture
        {
            Texture = _texture,
            PixelsPerUnit = 100f,
        };

        // Attach/display on the same entity this script is on.
        _spriteComponent = Entity.Get<SpriteComponent>();
        if (_spriteComponent == null)
        {
            _spriteComponent = new SpriteComponent();
            Entity.Add(_spriteComponent);
        }

        _spriteComponent.SpriteProvider = spriteFromTexture;
        _spriteComponent.PremultipliedAlpha = true;

        // 4) Initialize Skia side (CPU bitmap)
        var info = new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
        _bitmap = new SKBitmap(info);
        _canvas = new SKCanvas(_bitmap);

        _paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White,
        };

        _font = new SKFont(SKTypeface.Default, 28);

        _pixelBytes = new byte[W * H * 4];

        // Draw once so we see something immediately
        DrawWithSkiaAndUpload();
    }

    public override void Update()
    {
        _t += (float)Game.UpdateTime.Elapsed.TotalSeconds;

        // redraw every frame for a brutal “does this actually run?” test
        DrawWithSkiaAndUpload();
    }

    private void DrawWithSkiaAndUpload()
    {
        if (_texture == null || _bitmap == null || _canvas == null || _paint == null || _font == null || _pixelBytes == null)
            return;

        // --- Skia draw ---
        _canvas.Clear(new SKColor(20, 20, 26, 255));

        // fun little animated bar
        float bar = (float)(0.5 + 0.5 * Math.Sin(_t * 2.0));
        int barW = (int)(W * bar);

        using (var barPaint = new SKPaint { Color = new SKColor(80, 140, 255, 220), IsAntialias = true })
        {
            _canvas.DrawRoundRect(new SKRect(20, 40, 20 + barW, 80), 12, 12, barPaint);
        }

        // text (SkiaSharp 3.x wants SKFont + SKPaint)
        _paint.Color = SKColors.White;
        _canvas.DrawText($"SkiaSharp -> Stride Texture", 20, 130, _font, _paint);
        _canvas.DrawText($"t={_t:0.00}", 20, 165, _font, _paint);

        // corner label
        _paint.Color = new SKColor(255, 200, 80, 255);
        _canvas.DrawText("If you can read this, CPU Skia is working.", 20, 210, _font, _paint);

        // --- Copy pixels out of Skia bitmap (BGRA) ---
        IntPtr src = _bitmap.GetPixels();
        if (src == IntPtr.Zero)
            return;

        Marshal.Copy(src, _pixelBytes, 0, _pixelBytes.Length);

        // --- Upload to Stride texture ---
        // Texture.SetData copies CPU memory to GPU memory :contentReference[oaicite:9]{index=9}
        _texture.SetData(Game.GraphicsContext.CommandList, _pixelBytes);
    }

    private void DumpAssemblyProbe()
    {
        var names = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? "")
            .OrderBy(n => n)
            .ToArray();

        bool silkGfx =
            names.Contains("Silk.NET.Direct3D11", StringComparer.OrdinalIgnoreCase) ||
            names.Contains("Silk.NET.DXGI", StringComparer.OrdinalIgnoreCase);

        bool sharpGfx =
            names.Contains("SharpDX.Direct3D11", StringComparer.OrdinalIgnoreCase) ||
            names.Contains("SharpDX.DXGI", StringComparer.OrdinalIgnoreCase);

        bool sharpInput =
            names.Contains("SharpDX.XInput", StringComparer.OrdinalIgnoreCase) ||
            names.Contains("SharpDX.DirectInput", StringComparer.OrdinalIgnoreCase);

        Log.Info($"GFX bindings: Silk? {silkGfx} | SharpDX? {sharpGfx}");
        Log.Info($"Input bindings (SharpDX): {sharpInput}");

        foreach (var n in names.Where(n =>
                     n.StartsWith("Silk.NET", StringComparison.OrdinalIgnoreCase) ||
                     n.StartsWith("SharpDX", StringComparison.OrdinalIgnoreCase)))
            Log.Info($"  - {n}");
    }
    private void DumpVulkanCandidates(object obj, string name, int depth = 0)
    {
        if (obj == null || depth > 2) return;
        var t = obj.GetType();
        Log.Info($"{new string(' ', depth * 2)}{name}: {t.FullName}");

        // Print fields/properties that smell like Vulkan handles
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            if (!IsInteresting(p.Name, p.PropertyType)) continue;

            object? val = null;
            try { val = p.GetValue(obj); } catch { }
            Log.Info($"{new string(' ', depth * 2)}  P {p.Name} : {p.PropertyType.FullName} = {FormatVal(val)}");

            // Recurse into likely containers
            if (val != null && ShouldRecurse(val.GetType()))
                DumpVulkanCandidates(val, $"{name}.{p.Name}", depth + 1);
        }

        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!IsInteresting(f.Name, f.FieldType)) continue;

            object? val = null;
            try { val = f.GetValue(obj); } catch { }
            Log.Info($"{new string(' ', depth * 2)}  F {f.Name} : {f.FieldType.FullName} = {FormatVal(val)}");

            if (val != null && ShouldRecurse(val.GetType()))
                DumpVulkanCandidates(val, $"{name}.{f.Name}", depth + 1);
        }
    }

    private bool IsInteresting(string memberName, Type type)
    {
        var n = memberName.ToLowerInvariant();
        var tn = type.FullName?.ToLowerInvariant() ?? "";

        // keywords
        if (n.Contains("vulkan") || n.Contains("vk") || n.Contains("device") || n.Contains("instance") ||
            n.Contains("queue") || n.Contains("physical") || n.Contains("command") || n.Contains("native"))
            return true;

        // types
        if (tn.Contains("vortice.vulkan") || tn.Contains("vk") || tn.Contains("silk.net.vulkan"))
            return true;

        return false;
    }

    private bool ShouldRecurse(Type t)
    {
        var tn = t.FullName?.ToLowerInvariant() ?? "";
        return tn.Contains("stride.") || tn.Contains("vortice.vulkan");
    }

    private string FormatVal(object? v)
    {
        if (v == null) return "null";
        return v is string s ? s : v.ToString() ?? v.GetType().Name;
    }

}
