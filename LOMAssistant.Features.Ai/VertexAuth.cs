using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace LOMAssistant.Features.Ai;

/// <summary>
/// Vertex AI 的登录：它的 OpenAI 兼容接口不收固定的 Key，只认一小时有效的访问令牌。
/// 玩家在 Google Cloud 建一个服务账号、下载 JSON 密钥，把文件路径填在 Key 那一栏；
/// 这里用文件里的私钥签一张凭证（JWT）去换令牌，快到期前再换，玩家不用管。地址里的 {project} 换成文件里的项目编号。
/// </summary>
internal static class VertexAuth
{
	private const string Scope = "https://www.googleapis.com/auth/cloud-platform";
	private const string NotFound = "找不到服务账号密钥文件，请在「密钥文件」一栏填 JSON 文件的完整路径";
	private const string BadFile = "密钥文件读不了，请确认是从 Google Cloud 下载的服务账号 JSON 密钥";
	private const string Refused = "Google 没有给访问令牌，请确认密钥文件没有被删除、服务账号有 Vertex AI 用户权限";

	private static string _file, _token, _project;
	private static DateTime _expires;

	/// <summary>取访问令牌和项目编号；出错时 error 是给玩家看的原因。</summary>
	public static IEnumerator Token(string keyPath, Action<string, string, string> done)
	{
		string path = (keyPath ?? "").Trim().Trim('"');
		if (path.Length == 0 || !File.Exists(path))
		{
			done(null, null, NotFound);
			yield break;
		}
		if (_token != null && _file == path && DateTime.UtcNow < _expires)
		{
			done(_token, _project, null);
			yield break;
		}
		string email, project, tokenUri, assertion;
		try
		{
			JObject json = JObject.Parse(File.ReadAllText(path));
			email = (string)json["client_email"];
			project = (string)json["project_id"];
			tokenUri = (string)json["token_uri"] ?? "https://oauth2.googleapis.com/token";
			string pem = (string)json["private_key"];
			if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(project) || string.IsNullOrEmpty(pem))
			{
				done(null, null, BadFile);
				yield break;
			}
			assertion = Jwt(email, tokenUri, pem);
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[AI] 读 Vertex 密钥文件失败: " + ex.Message);
			done(null, null, BadFile);
			yield break;
		}
		byte[] form = Encoding.ASCII.GetBytes("grant_type=" + Uri.EscapeDataString("urn:ietf:params:oauth:grant-type:jwt-bearer") + "&assertion=" + assertion);
		var request = new UnityWebRequest(tokenUri, "POST")
		{
			uploadHandler = new UploadHandlerRaw(form),
			downloadHandler = new DownloadHandlerBuffer(),
			timeout = 30,
		};
		using (request)
		{
			request.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
			yield return request.SendWebRequest();
			string text = request.downloadHandler?.text ?? "";
			string token = null;
			int seconds = 0;
			try
			{
				JObject reply = text.Length > 0 ? JObject.Parse(text) : null;
				token = (string)reply?["access_token"];
				seconds = (int?)reply?["expires_in"] ?? 3600;
			}
			catch
			{
			}
			if (string.IsNullOrEmpty(token))
			{
				FileLogger.LogWarning("[AI] Vertex 换令牌失败: " + request.responseCode + " " + request.error + " " + text.Replace("\n", " "));
				done(null, null, Refused);
				yield break;
			}
			_file = path;
			_token = token;
			_project = project;
			// 提前五分钟换新的，免得一句话问到一半过期
			_expires = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 300));
			done(token, project, null);
		}
	}

	// 服务账号凭证：头、内容用 base64url 拼起来，再用私钥做 RS256 签名
	private static string Jwt(string email, string audience, string pem)
	{
		long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
		string head = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
		var claims = new JObject { ["iss"] = email, ["scope"] = Scope, ["aud"] = audience, ["iat"] = now, ["exp"] = now + 3600 };
		string body = Base64Url(Encoding.UTF8.GetBytes(claims.ToString(Formatting.None, new JsonConverter[0])));
		byte[] signed = Encoding.ASCII.GetBytes(head + "." + body);
		using (var rsa = new RSACryptoServiceProvider())
		{
			rsa.ImportParameters(PrivateKey(pem));
			var formatter = new RSAPKCS1SignatureFormatter(rsa);
			formatter.SetHashAlgorithm("SHA256");
			byte[] hash;
			using (SHA256 sha = SHA256.Create())
			{
				hash = sha.ComputeHash(signed);
			}
			return head + "." + body + "." + Base64Url(formatter.CreateSignature(hash));
		}
	}

	private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	// 「-----BEGIN PRIVATE KEY-----」的 PKCS#8 私钥：外层 SEQUENCE { 版本, 算法, OCTET STRING { RSAPrivateKey } }
	internal static RSAParameters PrivateKey(string pem)
	{
		var b64 = new StringBuilder();
		foreach (string line in pem.Replace("\\n", "\n").Split('\n'))
		{
			string l = line.Trim();
			if (l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal))
			{
				b64.Append(l);
			}
		}
		byte[] der = Convert.FromBase64String(b64.ToString());
		int pos = 0;
		Enter(der, ref pos, 0x30);
		Skip(der, ref pos, 0x02);
		Skip(der, ref pos, 0x30);
		Enter(der, ref pos, 0x04);
		Enter(der, ref pos, 0x30);
		Skip(der, ref pos, 0x02);
		byte[] n = Integer(der, ref pos), e = Integer(der, ref pos), d = Integer(der, ref pos);
		byte[] p = Integer(der, ref pos), q = Integer(der, ref pos);
		byte[] dp = Integer(der, ref pos), dq = Integer(der, ref pos), qi = Integer(der, ref pos);
		int half = (n.Length + 1) / 2;
		return new RSAParameters
		{
			Modulus = n,
			Exponent = e,
			D = Pad(d, n.Length),
			P = Pad(p, half),
			Q = Pad(q, half),
			DP = Pad(dp, half),
			DQ = Pad(dq, half),
			InverseQ = Pad(qi, half),
		};
	}

	private static int Length(byte[] der, ref int pos)
	{
		int first = der[pos++];
		if (first < 0x80)
		{
			return first;
		}
		int len = 0;
		for (int i = 0; i < (first & 0x7F); i++)
		{
			len = (len << 8) | der[pos++];
		}
		return len;
	}

	// 进到一个结构里面（只读掉标签和长度）
	private static void Enter(byte[] der, ref int pos, byte tag)
	{
		if (der[pos++] != tag)
		{
			throw new FormatException("私钥格式不对");
		}
		Length(der, ref pos);
	}

	private static void Skip(byte[] der, ref int pos, byte tag)
	{
		if (der[pos++] != tag)
		{
			throw new FormatException("私钥格式不对");
		}
		int len = Length(der, ref pos);
		pos += len;
	}

	// 整数去掉前面补的 0
	private static byte[] Integer(byte[] der, ref int pos)
	{
		if (der[pos++] != 0x02)
		{
			throw new FormatException("私钥格式不对");
		}
		int len = Length(der, ref pos);
		int start = pos;
		pos += len;
		while (len > 1 && der[start] == 0)
		{
			start++;
			len--;
		}
		var value = new byte[len];
		Array.Copy(der, start, value, 0, len);
		return value;
	}

	private static byte[] Pad(byte[] value, int size)
	{
		if (value.Length >= size)
		{
			return value;
		}
		var padded = new byte[size];
		Array.Copy(value, 0, padded, size - value.Length, value.Length);
		return padded;
	}
}
