#nullable enable

//
// Copyright (c) 2024 Frank A. Krueger
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
using System;
using System.Collections.Generic;
using System.Numerics;

using Metal;

using MetalKit;

namespace CrossGraphics.Metal
{
	[Foundation.Register ("MetalCanvas")]
	public class MetalCanvas : MTKView
	{
		public readonly IMTLDevice? CanvasDevice = MTLDevice.SystemDefault;
		private bool _drawsContinuously = false;

		public bool DrawsContinuously {
			get => _drawsContinuously;
			set
			{
				_drawsContinuously = value;
				OnDrawsContinuouslyChanged();
			}
		}

		public MetalCanvas (IntPtr handle) : base (handle)
		{
			Initialize ();
		}
		public MetalCanvas () : base ()
		{
			Initialize ();
		}
		void Initialize ()
		{
			Device = CanvasDevice;
			ColorPixelFormat = MetalGraphics.DefaultPixelFormat;
			if (Device is {} d) {
				if (d.SupportsTextureSampleCount (16)) {
					SampleCount = 16;
				}
				else if (d.SupportsTextureSampleCount (8)) {
					SampleCount = 8;
				}
				else if (d.SupportsTextureSampleCount (4)) {
					SampleCount = 4;
				}
				else if (d.SupportsTextureSampleCount (2)) {
					SampleCount = 2;
				}
				else {
					SampleCount = 1;
				}
			}
			ClearColor = new MTLClearColor (0.0, 0, 0.0, 0);
			AutoResizeDrawable = true;
			PreferredFramesPerSecond = 30;
			FramebufferOnly = true;
			PresentsWithTransaction = false;
			Delegate = new MetalCanvasDelegate (this);
			OnDrawsContinuouslyChanged ();
		}

		void OnDrawsContinuouslyChanged()
		{
			Paused = !DrawsContinuously;
			EnableSetNeedsDisplay = !DrawsContinuously;
		}

		public virtual void DrawMetalGraphics (MetalGraphics g)
		{
		}
	}

	public class MetalCanvasDelegate : MTKViewDelegate
	{
		readonly WeakReference<MetalCanvas> _canvas;
		MetalCanvas? Canvas => _canvas.TryGetTarget (out var c) ? c : null;
		public readonly IMTLCommandQueue? CommandQueue = MTLDevice.SystemDefault?.CreateCommandQueue ();
		MetalGraphicsBuffers? _buffers = null;
		public MetalCanvasDelegate (MetalCanvas canvas)
		{
			_canvas = new WeakReference<MetalCanvas> (canvas);
		}
		public override void DrawableSizeWillChange (MTKView view, global::CoreGraphics.CGSize size)
		{
		}
		public override void Draw (MTKView view)
		{
			if (view.Device is not { } device ||
			    view.CurrentRenderPassDescriptor is not { } renderPassDescriptor ||
			    view.CurrentDrawable is not { } drawable) {
				return;
			}

			using var commandBuffer = CommandQueue?.CommandBuffer ();
			if (commandBuffer is not null) {
				using var renderEncoder = commandBuffer.CreateRenderCommandEncoder (renderPassDescriptor);
				if (_buffers is null) {
					_buffers = new MetalGraphicsBuffers (device);
				}
				try {
					var bounds = view.Bounds;
					var g = new MetalGraphics (renderEncoder, (float)bounds.Width, (float)bounds.Height, _buffers);
					Canvas?.DrawMetalGraphics (g);
					g.EndDrawing ();
				}
				catch (Exception ex) {
					Console.WriteLine (ex);
				}
				renderEncoder.EndEncoding ();
				commandBuffer.PresentDrawable (drawable);
				commandBuffer.Commit ();
			}
		}
	}
}
