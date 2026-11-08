using HarmonyLib;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 鉴赏「音乐」页底部的播放条：暂停 / 继续、停止、进度（当前时间和总长）。
/// 曲子还是用游戏的 Wwise 事件（Music_曲名）播，只是自己发事件、拿到播放编号，才能查进度。
/// 游戏的音乐是 Wwise 的互动音乐（放在播放列表容器里循环），按分段查当前位置和长度；查不到再按普通音源查。
/// 进度只显示不能拖：Wwise 的播放列表、切换容器不支持跳转，试过 SeekOnEvent，返回成功但位置不动。
/// </summary>
public sealed partial class Gallery
{
	private uint _musicId;
	private float _musicLength; // 毫秒；普通音源时由回调给出，互动音乐时每次查分段信息
	private bool _musicPaused;
	private readonly AkSegmentInfo _musicSegment = new AkSegmentInfo();

	private const string MusicEventPrefix = "Music_";

	private void PlayTrack(Track track)
	{
		SoundManager sound = SoundManager.Instance;
		sound.StopMusic();
		sound.ResetMusic();
		// 游戏自己记着「当前曲名」，同名就不再播：记成这首，免得之后游戏又发一遍同一首
		Traverse.Create(sound).Field("_currentMusic").SetValue(track.Key);
		_musicLength = 0f;
		_musicPaused = false;
		uint flags = (uint)(AkCallbackType.AK_EnableGetSourcePlayPosition | AkCallbackType.AK_EnableGetMusicPlayPosition | AkCallbackType.AK_Duration);
		_musicId = AkSoundEngine.PostEvent(MusicEventPrefix + track.Key, sound.gameObject, flags, OnMusicCallback, null);
		_playing = track;
		_musicChanged = true;
	}

	private void StopTrack()
	{
		SoundManager.Instance.StopMusic();
		_musicId = 0;
		_musicPaused = false;
		_playing = null;
		_musicChanged = true;
	}

	private void OnMusicCallback(object cookie, AkCallbackType type, AkCallbackInfo info)
	{
		if (info is AkDurationCallbackInfo duration && duration.playingID == _musicId)
		{
			_musicLength = Mathf.Max(_musicLength, duration.fDuration);
		}
	}

	/// <summary>当前位置和总长（毫秒）；查不到返回 false。</summary>
	private bool MusicPosition(out int position, out int length)
	{
		position = length = 0;
		if (_musicId == 0)
		{
			return false;
		}
		if (AkSoundEngine.GetPlayingSegmentInfo(_musicId, _musicSegment, true) == AKRESULT.AK_Success && _musicSegment.iActiveDuration > 0)
		{
			position = _musicSegment.iCurrentPosition;
			length = _musicSegment.iActiveDuration;
			return true;
		}
		if (AkSoundEngine.GetSourcePlayPosition(_musicId, out int source) == AKRESULT.AK_Success && _musicLength > 0f)
		{
			position = source;
			length = Mathf.RoundToInt(_musicLength);
			return true;
		}
		return false;
	}

	private static string Clock(int ms)
	{
		int seconds = Mathf.Max(0, ms) / 1000;
		return seconds / 60 + ":" + (seconds % 60).ToString("00");
	}

	/// <summary>底部播放条：上面曲名，下面暂停 / 停止、进度条和时间。</summary>
	private void DrawMusicPlayer(Rect bar)
	{
		float buttonWidth = S(120f);
		var title = new Rect(bar.x, bar.y, bar.width, S(40f));
		GUI.Label(title, _playing != null ? string.Format(TextConverter.ForDisplay("正在播放：{0}"), _playing.Title) : TextConverter.ForDisplay("点曲名播放"), _body);

		float y = title.yMax + S(6f);
		GUI.enabled = _playing != null;
		var pause = new Rect(bar.x, y, buttonWidth, S(44f));
		if (GUI.Button(pause, TextConverter.ForDisplay(_musicPaused ? "继续" : "暂停"), _musicPaused ? _primary : _button))
		{
			if (_musicPaused)
			{
				SoundManager.Instance.ResumeMusic();
			}
			else
			{
				SoundManager.Instance.PauseMusic();
			}
			_musicPaused = !_musicPaused;
		}
		if (GUI.Button(new Rect(pause.xMax + S(8f), y, buttonWidth, S(44f)), TextConverter.ForDisplay("停止"), _button))
		{
			StopTrack();
		}
		GUI.enabled = true;

		float left = pause.xMax + buttonWidth + S(28f);
		var line = new Rect(left, y + S(20f), bar.xMax - left - S(150f), S(4f));
		bool known = MusicPosition(out int position, out int length);
		float t = known && length > 0 ? Mathf.Clamp01(position / (float)length) : 0f;
		Fill(line, PanelTheme.PaperDeep);
		Fill(new Rect(line.x, line.y, line.width * t, line.height), PanelTheme.Cinnabar);
		GUI.Label(new Rect(line.xMax + S(12f), y + S(4f), S(140f), S(36f)), known ? Clock(position) + " / " + Clock(length) : "--:-- / --:--", _muted);
	}
}
