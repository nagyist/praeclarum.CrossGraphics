using System.Drawing;

using CoreGraphics;

using CrossGraphics;

using Metal;

using LineBreakMode = CrossGraphics.LineBreakMode;
using PointF = System.Drawing.PointF;
using TextAlignment = CrossGraphics.TextAlignment;

namespace CrossGraphicsTests;

public class AcceptanceTests
{
    static readonly string OutputPath = GetOutputPath ();
    static readonly string AcceptedPath = Path.Combine(OutputPath, "AcceptedTests");
    static readonly string PendingPath = Path.Combine(OutputPath, "PendingTests");
    static string GetOutputPath ()
    {
	    var dir = Environment.GetCommandLineArgs ()[^1];
	    if (Path.GetFileName (dir) != "CrossGraphics") {
		    dir = Path.GetTempPath ();
	    }
	    return dir;
    }
    static AcceptanceTests()
    {
        if (!Directory.Exists(AcceptedPath))
            Directory.CreateDirectory(AcceptedPath);
        if (!Directory.Exists(PendingPath))
            Directory.CreateDirectory(PendingPath);
    }
    public void Setup()
    {
	    #if __MACOS__
	    AppKit.NSApplication.Init ();
	    #endif
    }

    record DrawArgs(IGraphics Graphics, int Width, int Height)
    {
    }

    class Drawing {
        public string Title { get; set; } = string.Empty;
        public Action<DrawArgs> Draw { get; set; } = _ => {};
    }

    abstract class Platform {
        public abstract string Name { get; }
        public abstract (IGraphics, object?) BeginDrawing(int width, int height);
        public abstract string SaveDrawing(IGraphics graphics, object context, string dir, string name);
    }

    class SvgPlatform : Platform
    {
        public override string Name => "SVG";
        public override (IGraphics, object?) BeginDrawing(int width, int height)
        {
            var w = new StringWriter();
            var g = new SvgGraphics(w, new Rectangle(0, 0, width, height));
            g.BeginDrawing();
            return (g, w);
        }
        public override string SaveDrawing(IGraphics graphics, object context, string dir, string name)
        {
            var fullName = name + ".svg";
            if (graphics is SvgGraphics svgGraphics && context is StringWriter writer)
            {
                svgGraphics.EndDrawing();
                var svg = writer.ToString();
                File.WriteAllText(Path.Combine(dir, fullName), svg);
            }
            return fullName;
        }
    }

    #if __MACOS__ || __IOS__ || __MACCATALYST__
    class CoreGraphicsPlatform : Platform
    {
        public override string Name => "CoreGraphicsUnflipped";
        public override (IGraphics, object?) BeginDrawing(int width, int height)
        {
            var cgContext = new CoreGraphics.CGBitmapContext(null, width, height, 8, width * 4, CoreGraphics.CGColorSpace.CreateDeviceRGB(), CoreGraphics.CGBitmapFlags.PremultipliedLast);
            var g = new CrossGraphics.CoreGraphics.CoreGraphicsGraphics(cgContext, highQuality: true, flipText: false);
            return (g, cgContext);
        }
        public override string SaveDrawing(IGraphics graphics, object context, string dir, string name)
        {
            var fullName = name + ".png";
            var url = Foundation.NSUrl.FromFilename (Path.Combine (dir, fullName));
            if (graphics is not CrossGraphics.CoreGraphics.CoreGraphicsGraphics coreGraphics ||
                context is not CoreGraphics.CGBitmapContext cgContext || cgContext.ToImage () is not { } cgImage ||
                ImageIO.CGImageDestination.Create (url, "public.png", 1) is not { } d) {
	            return fullName;
            }
            d.AddImage (cgImage, options: null);
            d.Close ();
            return fullName;
        }
    }
    class CoreGraphicsFlippedPlatform : Platform
    {
        public override string Name => "CoreGraphics";
        public override (IGraphics, object?) BeginDrawing(int width, int height)
        {
            var cgContext = new CoreGraphics.CGBitmapContext(null, width, height, 8, width * 4, CoreGraphics.CGColorSpace.CreateDeviceRGB(), CoreGraphics.CGBitmapFlags.PremultipliedLast);
            cgContext.TranslateCTM (0, height);
            cgContext.ScaleCTM (1, -1);
            var g = new CrossGraphics.CoreGraphics.CoreGraphicsGraphics(cgContext, highQuality: true, flipText: true);
            return (g, cgContext);
        }
        public override string SaveDrawing(IGraphics graphics, object context, string dir, string name)
        {
            var fullName = name + ".png";
            var url = Foundation.NSUrl.FromFilename (Path.Combine (dir, fullName));
            if (graphics is not CrossGraphics.CoreGraphics.CoreGraphicsGraphics coreGraphics ||
                context is not CoreGraphics.CGBitmapContext cgContext || cgContext.ToImage () is not { } cgImage ||
                ImageIO.CGImageDestination.Create (url, "public.png", 1) is not { } d) {
	            return fullName;
            }
            d.AddImage (cgImage, options: null);
            d.Close ();
            return fullName;
        }
    }
    #endif
    #if __IOS__ || __MACCATALYST__
    class UIGraphicsPlatform : Platform {
	    public override string Name => "UIGraphics";
	    public override (IGraphics, object?) BeginDrawing (int width, int height)
	    {
		    UIGraphics.BeginImageContextWithOptions(new CGSize (width, height), false, 1);
		    var graphics = new CrossGraphics.CoreGraphics.UIKitGraphics (highQuality: true);
		    return (graphics, null);
	    }
	    public override string SaveDrawing (IGraphics graphics, object context, string dir, string name)
	    {
		    var uiImage = UIGraphics.GetImageFromCurrentImageContext ();
		    UIGraphics.EndImageContext ();
		    var fullName = name + ".png";
		    uiImage.AsPNG ()?.Save (Path.Join (dir, fullName), atomically: true);
		    return fullName;
	    }
    }
    #endif
	class SkiaPlatform : Platform
	{
		public override string Name => "Skia";
		public override (IGraphics, object?) BeginDrawing (int width, int height)
		{
			var bitmap = new SkiaSharp.SKBitmap (width: width, height: height, isOpaque: false);
			var canvas = new SkiaSharp.SKCanvas (bitmap);
			var graphics = new CrossGraphics.Skia.SkiaGraphics (canvas);
			return (graphics, bitmap);
		}

		public override string SaveDrawing (IGraphics graphics, object context, string dir, string name)
		{
			var fullName = name + ".png";
			if (graphics is CrossGraphics.Skia.SkiaGraphics sg && context is SkiaSharp.SKBitmap bitmap) {
				sg.Canvas.Flush ();
				using var image = SkiaSharp.SKImage.FromBitmap (bitmap);
				using var data = image.Encode (SkiaSharp.SKEncodedImageFormat.Png, 100);
				using var stream = File.OpenWrite (Path.Combine (dir, fullName));
				data.SaveTo (stream);
			}

			return fullName;
		}
	}
	#if __MACOS__ || __IOS__ || __MACCATALYST__
	class MetalPlatform : Platform
	{
		private readonly Metal.IMTLDevice _device;
		private readonly Metal.IMTLCommandQueue _commandQueue;
		private readonly CrossGraphics.Metal.MetalGraphicsBuffers _buffers;
		public override string Name => "Metal";
		public MetalPlatform ()
		{
			_device = Metal.MTLDevice.SystemDefault!;
			_commandQueue = _device.CreateCommandQueue ()!;
			_buffers = new CrossGraphics.Metal.MetalGraphicsBuffers (_device);
		}
		
		record RenderContext (Metal.IMTLCommandBuffer CommandBuffer, Metal.IMTLRenderCommandEncoder RenderEncoder, Metal.IMTLTexture Texture);

		public override (IGraphics, object?) BeginDrawing (int width, int height)
		{
			var renderPassDescriptor = new Metal.MTLRenderPassDescriptor ();
			var tdesc = Metal.MTLTextureDescriptor.CreateTexture2DDescriptor (MTLPixelFormat.RGBA8Unorm, (UIntPtr)width,
				(UIntPtr)height, mipmapped: false);
			tdesc.Usage = Metal.MTLTextureUsage.RenderTarget;
			var texture = _device.CreateTexture (tdesc)!;
			renderPassDescriptor.ColorAttachments[0].Texture = texture;
			renderPassDescriptor.ColorAttachments[0].ClearColor = new Metal.MTLClearColor (0, 0, 0, 0);
			renderPassDescriptor.ColorAttachments[0].LoadAction = Metal.MTLLoadAction.Clear;
			renderPassDescriptor.ColorAttachments[0].StoreAction = Metal.MTLStoreAction.Store;
			var commandBuffer = _commandQueue.CommandBuffer ()!;
			var renderEncoder = commandBuffer.CreateRenderCommandEncoder (renderPassDescriptor);
			var g = new CrossGraphics.Metal.MetalGraphics (renderEncoder, width, height, _buffers);
			return (g, new RenderContext (commandBuffer, renderEncoder, texture));
		}

		public override string SaveDrawing (IGraphics graphics, object context, string dir, string name)
		{
			var fullName = name + ".png";
			if (graphics is CrossGraphics.Metal.MetalGraphics g && context is RenderContext rc) {
				g.EndDrawing ();
				rc.RenderEncoder.EndEncoding ();
				// commandBuffer.PresentDrawable (texture);
				rc.CommandBuffer.Commit ();
				rc.CommandBuffer.WaitUntilCompleted ();
				var cs = CoreGraphics.CGColorSpace.CreateDeviceRGB ();
				var bitmap = new CoreGraphics.CGBitmapContext (null, (IntPtr)rc.Texture.Width, (IntPtr)rc.Texture.Height, (IntPtr)8, (IntPtr)(4*rc.Texture.Width), cs, CoreGraphics.CGBitmapFlags.PremultipliedLast);
				rc.Texture.GetBytes (bitmap.Data, (UIntPtr)(4*rc.Texture.Width), new Metal.MTLRegion(
					new Metal.MTLOrigin { X = 0, Y = 0, Z = 0 },
					new Metal.MTLSize { Width = (IntPtr)rc.Texture.Width, Height = (IntPtr)rc.Texture.Height, Depth = (IntPtr)1 }),
				0);
				var cgImage = bitmap.ToImage ();
				var uiImage = new UIImage (cgImage);
				var data = uiImage.AsPNG ();
				data.Save (Path.Combine (dir, fullName), true);
			}
			return fullName;
		}
	}
	#endif

	static readonly Platform[] Platforms = {
        new SvgPlatform(),
        #if __MACOS__ || __IOS__ || __MACCATALYST__
        // new CoreGraphicsPlatform(),
        new CoreGraphicsFlippedPlatform (),
        #endif
        #if __IOS__ || __MACCATALYST__
        new UIGraphicsPlatform(),
        #endif
		new SkiaPlatform(),
        #if __MACOS__ || __IOS__ || __MACCATALYST__
        new MetalPlatform(),
        #endif
    };

    string Accept(string name, params Drawing[] drawings)
    {
        var width = 100;
        var height = 100;

        var w = new StringWriter();
        WriteHeader(name, w);
        w.WriteLine($"<h1><a href=\"index.html\">CrossGraphics Tests</a> / {name}</h1>");
        w.WriteLine($"<table border=\"0\" cellspacing=\"8\">");
        w.Write($"<tr><th>Drawing</th>");
        foreach (var platform in Platforms)
        {
	        w.Write ($"<th style=\"max-wdith:{width}\">{platform.Name}</th>");
        }
        w.WriteLine($"</tr>");
        
        foreach (var drawing in drawings) {
            w.Write($"<tr><th>{drawing.Title}</th>");
            foreach (var platform in Platforms) {
                var (graphics, context) = platform.BeginDrawing(width, height);
                drawing.Draw(new DrawArgs(graphics, width, height));
                var filename = platform.SaveDrawing(graphics, context, PendingPath, name + "_" + drawing.Title + "_" + platform.Name);
                var irender = filename.EndsWith (".svg") ? "smooth" : "crisp-edges";
                w.Write($"<td style=\"max-wdith:{width}\"><img src=\"{filename}\" alt=\"{drawing.Title} on {platform.Name}\" width=\"{width}\" height=\"{height}\" image-rendering=\"{irender}\" /></td>");
            }
            w.WriteLine("</tr>");
        }
    
        w.WriteLine("</table>");
        w.WriteLine("</body></html>");
        var pendingHTML = w.ToString();
        string outFileName = name + ".html";
        File.WriteAllText(Path.Combine(PendingPath, outFileName), pendingHTML);
        return outFileName;
    }

    private static void WriteHeader(string title, StringWriter w)
    {
	    w.WriteLine($"<html><head><title>{title}</title>");
	    w.WriteLine($"<style>");
	    w.WriteLine($"html {{ font-family: sans-serif; font-size: 12px; background-color: #333; color: #fff; }}");
	    w.WriteLine($"table {{ font-size: 12px; }}");
	    w.WriteLine($"img {{ background-color: #fff; }}");
	    w.WriteLine($"a {{ color: #ccf; }}");
	    w.WriteLine($"a:visited {{ color: #ccf; }}");
	    w.WriteLine($"</style>");
	    w.WriteLine($"</head><body>");
    }

    public void Run ()
    {
	    var pages = new string[] {
		    Arcs (),
		    Lines (),
		    Ovals (),
		    Polygons (),
		    Rects (),
		    RoundedRects (),
		    Text ()
	    };
	    var w = new StringWriter();
	    WriteHeader("Cross Graphics Tests", w);
	    w.WriteLine($"<h1>CrossGraphics Tests</h1>");
	    w.WriteLine($"<ul>");
	    foreach (var p in pages) {
			w.WriteLine($"<li><a href=\"{p}\">{p}</li>");
	    }
	    w.WriteLine($"</ul>");
	    w.WriteLine("</body></html>");
	    var pendingHTML = w.ToString();
	    string outFileName = "index.html";
	    File.WriteAllText(Path.Combine(PendingPath, outFileName), pendingHTML);
    }

    string Arcs()
    {
        Drawing Make(float startAngle, float endAngle, float w=5) {
            return new Drawing {
                Title = $"Arc_S{startAngle*180.0f/MathF.PI:F2}_E{endAngle*180.0f/MathF.PI:F2}",
                Draw = args => {
                    args.Graphics.SetRgba(0, 0, 128, 255);
                    var radius = MathF.Min(args.Width, args.Height) * 0.2f;
                    var cxs = args.Width / 2 - radius - w/2;
                    var cxf = args.Width / 2 + radius + w/2;
                    var cy = args.Height / 2;
                    args.Graphics.DrawArc(cxs, cy, radius, startAngle, endAngle, w);
                    args.Graphics.FillArc(cxf, cy, radius, startAngle, endAngle);
                }
            };
        }
	    return Accept("Arcs",
		    Make (0, MathF.PI * 2.00f),
		    Make (0, MathF.PI * 2.25f),
		    Make (-MathF.PI * 0.25f, MathF.PI * 2.25f),
		    Make (MathF.PI * 1.25f, -MathF.PI / 180.0f * 120.0f),
		    Make (MathF.PI * 1.25f, -MathF.PI / 180.0f * 135.0f),
		    Make (MathF.PI * 1.25f, -MathF.PI / 180.0f * 150.0f),
		    Make (MathF.PI * 1.25f, -MathF.PI / 180.0f * 179.0f),
		    Make (MathF.PI * 1.25f, -MathF.PI / 180.0f * 180.0f),
		    Make (MathF.PI * 1.25f, -MathF.PI / 180.0f * 181.0f),
		    Make (MathF.PI * 1.25f, -MathF.PI * 1.25f),
		    Make (MathF.PI * 1.25f, -MathF.PI * 2.50f),
		    Make (MathF.PI * 1.25f, -MathF.PI * 2.75f),
		    Make (0, MathF.PI * 1.75f),
		    Make (0, MathF.PI * 1.50f),
		    Make (0, MathF.PI * 1.25f),
		    Make (0, MathF.PI * 1.00f),
		    Make (0, MathF.PI * 0.75f),
		    Make (0, MathF.PI * 0.50f),
		    Make (0, MathF.PI * 0.25f),
		    Make (0, MathF.PI * 0.00f),
		    Make (MathF.PI * 1.25f, MathF.PI * 2.00f),
		    Make (MathF.PI * 1.25f, MathF.PI * 1.75f),
		    Make (MathF.PI * 1.25f, MathF.PI * 1.50f),
		    Make (MathF.PI * 1.25f, MathF.PI * 1.25f),
		    Make (MathF.PI * 1.25f, MathF.PI * 1.00f),
		    Make (MathF.PI * 1.25f, MathF.PI * 0.75f),
		    Make (MathF.PI * 1.25f, MathF.PI * 0.50f),
		    Make (MathF.PI * 1.25f, MathF.PI * 0.25f),
		    Make (MathF.PI * 1.25f, MathF.PI * 0.00f),
		    Make (MathF.PI * 1.25f, -MathF.PI * 2.00f),
		    Make (MathF.PI * 1.25f, -MathF.PI * 1.75f),
		    Make (MathF.PI * 1.25f, -MathF.PI * 1.50f),
		    Make (MathF.PI * 1.25f, -MathF.PI * 1.25f),
		    Make (MathF.PI * 1.25f, -MathF.PI * 1.00f),
		    Make (MathF.PI * 1.25f, -MathF.PI * 0.75f),
		    Make (MathF.PI * 1.25f, -MathF.PI * 0.50f),
		    Make (MathF.PI * 1.25f, -MathF.PI * 0.25f),
		    Make (MathF.PI * 1.25f, -MathF.PI * 0.00f),
		    Make (-MathF.PI * 1.25f, MathF.PI * 2.00f),
		    Make (-MathF.PI * 1.25f, MathF.PI * 1.75f),
		    Make (-MathF.PI * 1.25f, MathF.PI * 1.50f),
		    Make (-MathF.PI * 1.25f, MathF.PI * 1.25f),
		    Make (-MathF.PI * 1.25f, MathF.PI * 1.00f),
		    Make (-MathF.PI * 1.25f, MathF.PI * 0.75f),
		    Make (-MathF.PI * 1.25f, MathF.PI * 0.50f),
		    Make (-MathF.PI * 1.25f, MathF.PI * 0.25f),
		    Make (-MathF.PI * 1.25f, MathF.PI * 0.00f),
		    Make (-MathF.PI * 1.25f, -MathF.PI * 2.00f),
		    Make (-MathF.PI * 1.25f, -MathF.PI * 1.75f),
		    Make (-MathF.PI * 1.25f, -MathF.PI * 1.50f),
		    Make (-MathF.PI * 1.25f, -MathF.PI * 1.25f),
		    Make (-MathF.PI * 1.25f, -MathF.PI * 1.00f),
		    Make (-MathF.PI * 1.25f, -MathF.PI * 0.75f),
		    Make (-MathF.PI * 1.25f, -MathF.PI * 0.50f),
		    Make (-MathF.PI * 1.25f, -MathF.PI * 0.25f),
		    Make (-MathF.PI * 1.25f, -MathF.PI * 0.00f)
        );
    }

    string Ovals()
    {
        Drawing Make(float width, float height, float w) {
            return new Drawing {
                Title = $"Oval_W{width:F2}_H{height:F2}_L{w:F2}",
                Draw = args => {
	                args.Graphics.SetRgba (0, 0, 128, 255);
	                var x = args.Width / 2 - width / 2;
	                var y = args.Height / 2 - height / 2;
	                if (w < 0)
	                {
		                args.Graphics.FillOval(new RectangleF(x, y, width, height));
	                }
	                else {
		                args.Graphics.DrawOval (new RectangleF (x, y, width, height), w);
	                }
                }
            };
        }
	    return Accept("Ovals",
            Make(50, 50, -1),
            Make(50, 5, -1),
            Make(5, 50, -1),
            Make(50, 50, 0.125f),
            Make(50, 50, 0.25f),
            Make(50, 50, 0.333f),
            Make(50, 50, 0.75f),
            Make(50, 50, 1),
            Make(50, 50, 1.333f),
            Make(50, 50, 10),
            Make(50, 50, 50),
            Make(50, 100, 1),
            Make(50, 100, 10),
            Make(50, 100, 50),
            Make(100, 100, 1),
            Make(100, 100, 10),
            Make(100, 100, 50)
        );
    }

    string Lines()
    {
        Drawing MakeHs(float yoff, float w) {
            return new Drawing {
                Title = $"HLine_O{yoff:F2}_L{w:F2}",
                Draw = args => {
                    args.Graphics.SetRgba(0, 0, 128, 255);
                    var n = 7;
                    var y = args.Height / 2 - n * w * 6;
                    for (var i = 0; i < n; i++) {
	                    args.Graphics.DrawLine(20, y + yoff, args.Width-20, y, w*(i + 1));
	                    y += 4 * w * (i + 1);
                    }
                }
            };
        }
        Drawing MakeVs(float xoff, float w) {
			return new Drawing {
				Title = $"VLine_O{xoff:F2}_L{w:F2}",
				Draw = args => {
					args.Graphics.SetRgba(0, 0, 128, 255);
					var n = 7;
					var x = args.Width / 2 - n * w * 6;
					for (var i = 0; i < n; i++) {
	                    args.Graphics.DrawLine(x + xoff, 20, x, args.Height-20, w*(i + 1));
	                    x += 4 * w * (i + 1);
					}
				}
			};
		}	
	    return Accept("Lines",
            MakeHs(0.00f, 0.25f),
            MakeHs(0.00f, 0.50f),
            MakeHs(0.00f, 0.75f),
            MakeHs(0.00f, 1.00f),
            MakeHs(0.50f, 0.25f),
            MakeHs(0.50f, 0.50f),
            MakeHs(0.50f, 0.75f),
            MakeHs(0.50f, 1.00f),
            MakeVs(0.00f, 0.25f),
            MakeVs(0.00f, 0.50f),
            MakeVs(0.00f, 0.75f),
            MakeVs(0.00f, 1.00f),
            MakeVs(0.50f, 0.25f),
            MakeVs(0.50f, 0.50f),
            MakeVs(0.50f, 0.75f),
            MakeVs(0.50f, 1.00f)
        );
    }

    string Polygons()
    {
	    Drawing Make(float alpha, float w) {
		    return new Drawing {
			    Title = $"Tri_A{alpha:F2}_L{w:F2}",
			    Draw = args => {
				    args.Graphics.SetRgba (0, 0, 128, (byte)MathF.Round(255 * alpha));
				    var poly = new Polygon (3);
				    poly.Points.Add (new PointF (30, 70));
				    poly.Points.Add (new PointF (60.5f, 60.5f));
				    poly.Points.Add (new PointF (50.0f, 21.0f));
				    if (w < 0)
				    {
						args.Graphics.FillPolygon (poly);
				    }
				    else {
						args.Graphics.DrawPolygon (poly, w);
				    }
			    }
		    };
	    }
	    return Accept("Polygons",
		    Make(1.0f, -1),
		    Make(1.0f, 0.125f),
		    Make(1.0f, 0.333f),
		    Make(1.0f, 1.75f),
		    Make(1.0f, 4.5f),
		    Make(0.5f, -1),
		    Make(0.5f, 0.125f),
		    Make(0.5f, 0.333f),
		    Make(0.5f, 1.75f),
		    Make(0.5f, 4.5f)
	    );
    }

    string Rects()
    {
        Drawing Make(float width, float height, float w) {
            return new Drawing {
                Title = $"Rect_W{width:F2}_H{height:F2}_L{w:F2}",
                Draw = args => {
                    args.Graphics.SetRgba(0, 0, 128, 255);
                    var x = args.Width / 2 - width / 2;
                    var y = args.Height / 2 - height / 2;
                    if (w < 0) {
	                    args.Graphics.FillRect (new RectangleF (x, y, width, height));
                    }
                    else {
	                    args.Graphics.DrawRect (new RectangleF (x, y, width, height), w);
                    }
                }
            };
        }
	    return Accept("Rects",
            Make(49.5f, 49.5f, -1),
            Make(49.75f, 49.75f, -1),
            Make(50, 50, -1),
            Make(48, 48, 0.125f),
            Make(48, 48, 0.25f),
            Make(48, 48, 0.3333f),
            Make(48, 48, 0.75f),
            Make(48, 48, 1),
            Make(48, 48, 1.125f),
            Make(48, 48, 1.333f),
            Make(49, 49, 1),
            Make(50, 50, 1),
            Make(50, 50, 10),
            Make(50, 50, 50),
            Make(50, 100, -1),
            Make(50, 100, 1),
            Make(50, 100, 10),
            Make(50, 100, 50),
            Make(100, 100, -1),
            Make(100, 100, 1),
            Make(100, 100, 10),
            Make(100, 100, 50)
        );
    }

    string RoundedRects()
    {
        Drawing Make(float width, float height, float r, float w) {
            return new Drawing {
                Title = $"RRect_W{width:F2}_H{height:F2}_L{w:F2}",
                Draw = args => {
                    args.Graphics.SetRgba(0, 0, 128, 255);
                    var x = args.Width / 2 - width / 2;
                    var y = args.Height / 2 - height / 2;
                    if (w < 0) {
	                    args.Graphics.FillRoundedRect (new RectangleF (x, y, width, height), r);
                    }
                    else {
	                    args.Graphics.DrawRoundedRect (new RectangleF (x, y, width, height), r, w);
                    }
                }
            };
        }
	    return Accept("RoundedRects",
            Make(50, 50, 10, -1),
            Make(48, 48, 10, 1),
            Make(49, 49, 10, 1),
            Make(50, 50, 10, 1),
            Make(50, 50, 10, 10),
            Make(50, 50, 10, 50),
            Make(50, 25, 10, -1),
            Make(50, 25, 10, 1),
            Make(50, 25, 10, 10),
            Make(50, 25, 10, 50),
            Make(64, 64, 2, -1),
            Make(64, 64, 2, 1),
            Make(64, 64, 2, 10),
            Make(64, 64, 2, 50),
            Make(50, 100, 25, -1),
            Make(50, 100, 25, 1),
            Make(50, 100, 25, 10),
            Make(50, 100, 25, 50),
            Make(100, 100, 50, -1),
            Make(100, 100, 50, 1),
            Make(100, 100, 50, 10),
            Make(100, 100, 50, 50)
        );
    }

    string Text()
    {
	    string singleLine = "A single line of text.";
        Drawing MakeRect(string s, string? fontFamily, int fontSize, LineBreakMode lineBreakMode, TextAlignment align) {
            return new Drawing {
                Title = $"TextRect_{s.Length}_F{fontFamily}_S{fontSize}_{lineBreakMode}_{align}",
                Draw = args => {
                    args.Graphics.SetRgba(0, 0, 0, 128);
                    var pad = 4;
                    args.Graphics.DrawRect (pad, pad, args.Width - pad * 2, args.Height - pad * 2, 1);
                    var font = fontFamily is {} fn ? CrossGraphics.Font.BoldUserFixedPitchFontOfSize (fontSize) : CrossGraphics.Font.SystemFontOfSize (fontSize);
                    args.Graphics.SetFont (font);
                    var fm = args.Graphics.GetFontMetrics ();
                    args.Graphics.SetRgba(0, 255, 0, 128);
                    args.Graphics.DrawLine (pad, pad + fm.Ascent, args.Width - pad, pad + fm.Ascent, 1);
                    args.Graphics.SetRgba(255, 0, 0, 128);
                    args.Graphics.DrawLine (pad, pad + fm.Ascent + fm.Descent, args.Width - pad, pad + fm.Ascent + fm.Descent, 1);
                    args.Graphics.SetRgba(0, 0, 128, 255);
                    args.Graphics.DrawString (s, pad, pad, args.Width-2*pad, args.Height-2*pad,lineBreakMode, align);
                }
            };
        }
        var otherFam = "BoldUserFixedPitch";
	    return Accept("Text",
		    MakeRect (singleLine, null, 14, LineBreakMode.None, TextAlignment.Left),
		    MakeRect (singleLine, null, 14, LineBreakMode.None, TextAlignment.Center),
		    MakeRect (singleLine, null, 14, LineBreakMode.None, TextAlignment.Right),
		    MakeRect (singleLine, null, 22, LineBreakMode.None, TextAlignment.Left),
		    MakeRect (singleLine, null, 22, LineBreakMode.None, TextAlignment.Center),
		    MakeRect (singleLine, null, 22, LineBreakMode.None, TextAlignment.Right),
		    MakeRect (singleLine, otherFam, 14, LineBreakMode.None, TextAlignment.Left),
		    MakeRect (singleLine, otherFam, 14, LineBreakMode.None, TextAlignment.Center),
		    MakeRect (singleLine, otherFam, 14, LineBreakMode.None, TextAlignment.Right),
		    MakeRect (singleLine, otherFam, 22, LineBreakMode.None, TextAlignment.Left),
		    MakeRect (singleLine, otherFam, 22, LineBreakMode.None, TextAlignment.Center),
		    MakeRect (singleLine, otherFam, 22, LineBreakMode.None, TextAlignment.Right)
        );
    }
}
