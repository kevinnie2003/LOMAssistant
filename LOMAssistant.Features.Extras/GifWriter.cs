using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 极简 GIF89a 编码器：每帧带自己的调色板（Local Color Table），保留一位透明，供鉴赏「动图」页导出用。
/// Unity 里没有 System.Drawing，所以量化（中位切分）和 LZW 都自己写，不引第三方库。
/// 透明判定：alpha 低于阈值算全透明，写成调色板最后一个索引；半透明一律按不透明处理（GIF 只有 1 位透明）。
/// </summary>
internal static class GifWriter
{
	/// <summary>每帧调色板里留给透明的索引：不透明颜色最多占 0–254。</summary>
	private const byte TransparentIndex = 255;

	private const int MaxColors = 255;

	/// <summary>
	/// 把若干帧写成一张 GIF。帧可以尺寸不同，统一按最大的宽高居中，空出来的地方透明。
	/// </summary>
	/// <param name="frames">每帧的像素，长度是各自宽×高，顺序为左上到右下</param>
	/// <param name="bottomUp">帧数据的第 0 行在底部（Unity 的 Texture2D.GetPixels32 就是这样），写 GIF 时翻过来</param>
	public static void Save(string path, IReadOnlyList<(Color32[] Pixels, int Width, int Height)> frames, int delayMs, bool bottomUp = true)
	{
		if (frames == null || frames.Count == 0)
		{
			throw new ArgumentException("没有可写的帧");
		}
		int width = 0, height = 0;
		foreach (var frame in frames)
		{
			width = Math.Max(width, frame.Width);
			height = Math.Max(height, frame.Height);
		}
		if (width <= 0 || height <= 0 || width > 65535 || height > 65535)
		{
			throw new ArgumentException("尺寸超出 GIF 上限");
		}

		using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
		using var writer = new BinaryWriter(stream);
		writer.Write(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
		writer.Write((ushort)width);
		writer.Write((ushort)height);
		writer.Write((byte)0x70); // 没有全局调色板，颜色深度 8
		writer.Write((byte)0);
		writer.Write((byte)0);

		WriteLoopExtension(writer);

		var canvas = new Color32[width * height];
		var transparent = new Color32(0, 0, 0, 0);
		foreach (var frame in frames)
		{
			Array.Fill(canvas, transparent);
			int offsetX = (width - frame.Width) / 2, offsetY = (height - frame.Height) / 2;
			for (int y = 0; y < frame.Height; y++)
			{
				// GIF 的第 0 行在顶部，Unity 的第 0 行在底部，这里翻一次
				int row = bottomUp ? height - 1 - (y + offsetY) : y + offsetY;
				Array.Copy(frame.Pixels, y * frame.Width, canvas, row * width + offsetX, frame.Width);
			}
			WriteFrame(writer, canvas, width, height, delayMs);
		}
		writer.Write((byte)0x3B); // trailer
	}

	private static void WriteLoopExtension(BinaryWriter writer)
	{
		writer.Write((byte)0x21);
		writer.Write((byte)0xFF);
		writer.Write((byte)11);
		writer.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
		writer.Write((byte)3);
		writer.Write((byte)1);
		writer.Write((ushort)0); // 0 = 一直循环
		writer.Write((byte)0);
	}

	private static void WriteFrame(BinaryWriter writer, Color32[] pixels, int width, int height, int delayMs)
	{
		byte[] indices = Quantize(pixels, out byte[] palette, out int paletteSize);

		// 调色板大小必须是 2 的幂，且至少 2 色
		int bits = 1;
		while ((1 << bits) < paletteSize)
		{
			bits++;
		}
		int tableSize = 1 << bits;
		var table = new byte[tableSize * 3];
		Array.Copy(palette, table, Math.Min(palette.Length, table.Length));

		// 图形控制扩展：延时 + 透明
		writer.Write((byte)0x21);
		writer.Write((byte)0xF9);
		writer.Write((byte)4);
		writer.Write((byte)0x09); // 处置方式 2（还原为背景）+ 透明标志
		// 延时的单位是 1/100 秒。这里有两个坑：
		// ① 直接除 10 是向下取整，18ms 会变成 1（10ms），帧率高时误差大到几倍，所以四舍五入；
		// ② 延时 ≤1（10ms）会被几乎所有查看器当成 100ms 慢放（GIF 的老问题），所以下限取 2（20ms，即 50 帧/秒）。
		writer.Write((ushort)Math.Min(65535, Math.Max(2, (delayMs + 5) / 10)));
		writer.Write(TransparentIndex);
		writer.Write((byte)0);

		// 图像描述符：带局部调色板
		writer.Write((byte)0x2C);
		writer.Write((ushort)0);
		writer.Write((ushort)0);
		writer.Write((ushort)width);
		writer.Write((ushort)height);
		writer.Write((byte)(0x80 | (bits - 1))); // 局部调色板标志 + 大小
		writer.Write(table);

		WriteLzw(writer, indices, 8);
	}

	/// <summary>把一帧量化成 0–254 的调色板索引，透明像素写 <see cref="TransparentIndex"/>。</summary>
	private static byte[] Quantize(Color32[] pixels, out byte[] palette, out int paletteSize)
	{
		// 1) 收集不透明颜色，按 5 位量化去重（32768 个桶），同时记下每桶的代表色和像素数
		var counts = new int[32768];
		var sums = new long[32768 * 3];
		for (int i = 0; i < pixels.Length; i++)
		{
			Color32 c = pixels[i];
			if (c.a < 128)
			{
				continue;
			}
			int bucket = ((c.r >> 3) << 10) | ((c.g >> 3) << 5) | (c.b >> 3);
			counts[bucket]++;
			sums[bucket * 3] += c.r;
			sums[bucket * 3 + 1] += c.g;
			sums[bucket * 3 + 2] += c.b;
		}

		var colors = new List<(byte R, byte G, byte B, int Count)>();
		for (int i = 0; i < counts.Length; i++)
		{
			if (counts[i] == 0)
			{
				continue;
			}
			colors.Add(((byte)(sums[i * 3] / counts[i]), (byte)(sums[i * 3 + 1] / counts[i]), (byte)(sums[i * 3 + 2] / counts[i]), counts[i]));
		}

		List<(byte R, byte G, byte B)> chosen;
		if (colors.Count == 0)
		{
			chosen = new List<(byte, byte, byte)> { (0, 0, 0) };
		}
		else if (colors.Count <= MaxColors)
		{
			chosen = new List<(byte, byte, byte)>();
			foreach (var c in colors)
			{
				chosen.Add((c.R, c.G, c.B));
			}
		}
		else
		{
			chosen = MedianCut(colors, MaxColors);
		}

		paletteSize = chosen.Count + 1; // 末尾留一个透明色
		palette = new byte[paletteSize * 3];
		for (int i = 0; i < chosen.Count; i++)
		{
			palette[i * 3] = chosen[i].R;
			palette[i * 3 + 1] = chosen[i].G;
			palette[i * 3 + 2] = chosen[i].B;
		}

		// 2) 建 5 位颜色 → 调色板索引的查找表，再逐像素查
		var lookup = new byte[32768];
		var done = new bool[32768];
		var result = new byte[pixels.Length];
		for (int i = 0; i < pixels.Length; i++)
		{
			Color32 c = pixels[i];
			if (c.a < 128)
			{
				result[i] = TransparentIndex;
				continue;
			}
			int bucket = ((c.r >> 3) << 10) | ((c.g >> 3) << 5) | (c.b >> 3);
			if (!done[bucket])
			{
				lookup[bucket] = Nearest(chosen, c);
				done[bucket] = true;
			}
			result[i] = lookup[bucket];
		}
		return result;
	}

	private static byte Nearest(List<(byte R, byte G, byte B)> palette, Color32 color)
	{
		int best = 0, bestDistance = int.MaxValue;
		for (int i = 0; i < palette.Count; i++)
		{
			int dr = palette[i].R - color.r, dg = palette[i].G - color.g, db = palette[i].B - color.b;
			int distance = dr * dr + dg * dg + db * db;
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = i;
				if (distance == 0)
				{
					break;
				}
			}
		}
		return (byte)best;
	}

	/// <summary>中位切分：反复挑像素最多的盒子、沿最长的颜色轴切两半，直到盒子数够了。</summary>
	private static List<(byte R, byte G, byte B)> MedianCut(List<(byte R, byte G, byte B, int Count)> colors, int max)
	{
		var boxes = new List<List<(byte R, byte G, byte B, int Count)>> { colors };
		while (boxes.Count < max)
		{
			int pick = -1, bestScore = 0;
			for (int i = 0; i < boxes.Count; i++)
			{
				if (boxes[i].Count < 2)
				{
					continue;
				}
				int score = 0;
				foreach (var c in boxes[i])
				{
					score += c.Count;
				}
				if (score > bestScore)
				{
					bestScore = score;
					pick = i;
				}
			}
			if (pick < 0)
			{
				break;
			}
			var box = boxes[pick];
			int axis = LongestAxis(box);
			box.Sort((a, b) => axis == 0 ? a.R.CompareTo(b.R) : axis == 1 ? a.G.CompareTo(b.G) : a.B.CompareTo(b.B));
			int total = 0;
			foreach (var c in box)
			{
				total += c.Count;
			}
			int running = 0, split = 1;
			for (int i = 0; i < box.Count - 1; i++)
			{
				running += box[i].Count;
				if (running * 2 >= total)
				{
					split = i + 1;
					break;
				}
			}
			var right = box.GetRange(split, box.Count - split);
			box.RemoveRange(split, box.Count - split);
			boxes.Add(right);
		}

		var result = new List<(byte R, byte G, byte B)>();
		foreach (var box in boxes)
		{
			long r = 0, g = 0, b = 0, n = 0;
			foreach (var c in box)
			{
				r += (long)c.R * c.Count;
				g += (long)c.G * c.Count;
				b += (long)c.B * c.Count;
				n += c.Count;
			}
			if (n > 0)
			{
				result.Add(((byte)(r / n), (byte)(g / n), (byte)(b / n)));
			}
		}
		return result;
	}

	private static int LongestAxis(List<(byte R, byte G, byte B, int Count)> box)
	{
		byte minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
		foreach (var c in box)
		{
			minR = Math.Min(minR, c.R); maxR = Math.Max(maxR, c.R);
			minG = Math.Min(minG, c.G); maxG = Math.Max(maxG, c.G);
			minB = Math.Min(minB, c.B); maxB = Math.Max(maxB, c.B);
		}
		int spanR = maxR - minR, spanG = maxG - minG, spanB = maxB - minB;
		if (spanR >= spanG && spanR >= spanB)
		{
			return 0;
		}
		return spanG >= spanB ? 1 : 2;
	}

	/// <summary>GIF 的 LZW：码长从 9 位起，满 12 位就发 clear 重来；数据按 255 字节一块分包。</summary>
	private static void WriteLzw(BinaryWriter writer, byte[] indices, int minCodeSize)
	{
		writer.Write((byte)minCodeSize);
		var bits = new BitWriter(writer);
		int clearCode = 1 << minCodeSize;
		int endCode = clearCode + 1;
		int codeSize = minCodeSize + 1;
		int next = endCode + 1;
		var table = new Dictionary<int, int>();

		bits.Write(clearCode, codeSize);
		if (indices.Length == 0)
		{
			bits.Write(endCode, codeSize);
			bits.Finish();
			return;
		}

		int prefix = indices[0];
		for (int i = 1; i < indices.Length; i++)
		{
			int k = indices[i];
			int key = (prefix << 8) | k;
			if (table.TryGetValue(key, out int code))
			{
				prefix = code;
				continue;
			}
			bits.Write(prefix, codeSize);
			if (next < 4096)
			{
				table[key] = next++;
				if (next > (1 << codeSize) && codeSize < 12)
				{
					codeSize++;
				}
			}
			else
			{
				bits.Write(clearCode, codeSize);
				table.Clear();
				codeSize = minCodeSize + 1;
				next = endCode + 1;
			}
			prefix = k;
		}
		bits.Write(prefix, codeSize);
		bits.Write(endCode, codeSize);
		bits.Finish();
	}

	/// <summary>按 LSB 优先把变长码写进 255 字节一块的子块流。</summary>
	private sealed class BitWriter
	{
		private readonly BinaryWriter _writer;
		private readonly byte[] _block = new byte[255];
		private int _blockLength;
		private int _bits, _count;

		public BitWriter(BinaryWriter writer)
		{
			_writer = writer;
		}

		public void Write(int code, int size)
		{
			_bits |= code << _count;
			_count += size;
			while (_count >= 8)
			{
				Push((byte)(_bits & 0xFF));
				_bits >>= 8;
				_count -= 8;
			}
		}

		private void Push(byte value)
		{
			_block[_blockLength++] = value;
			if (_blockLength == 255)
			{
				FlushBlock();
			}
		}

		private void FlushBlock()
		{
			if (_blockLength == 0)
			{
				return;
			}
			_writer.Write((byte)_blockLength);
			_writer.Write(_block, 0, _blockLength);
			_blockLength = 0;
		}

		public void Finish()
		{
			if (_count > 0)
			{
				Push((byte)(_bits & 0xFF));
				_bits = 0;
				_count = 0;
			}
			FlushBlock();
			_writer.Write((byte)0);
		}
	}
}
