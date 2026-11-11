using System;
using System.Collections.Generic;

namespace LOMAssistant.Features.Combat;

/// <summary>只保留本场实际显示过的对白；同一句在后面的回合再次说出时仍然记录。</summary>
internal sealed class DuelDialogueHistory
{
	internal const int Limit = 500;
	internal sealed class Entry
	{
		internal int Round;
		internal string Speaker;
		internal string Text;
	}

	private readonly List<Entry> _entries = new List<Entry>();
	internal IReadOnlyList<Entry> Entries => _entries;

	internal bool Add(int round, string speaker, string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		_entries.Add(new Entry { Round = Math.Max(0, round), Speaker = speaker ?? "", Text = text });
		if (_entries.Count > Limit)
		{
			_entries.RemoveRange(0, _entries.Count - Limit);
		}
		return true;
	}

	internal void Clear() => _entries.Clear();
}
