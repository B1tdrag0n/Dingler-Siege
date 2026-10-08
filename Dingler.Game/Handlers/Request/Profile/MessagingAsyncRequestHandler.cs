extern alias HexGame;
using System.Collections;
using System.Reflection;
using System.Text;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Shared.Campaign.Siege;
using HexGame::Game.Shared.Network;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Profile;

// Siege (Milestone 1): we don't know the wire shape of Messaging.Request yet, so this only logs every field of each
// request it receives, without guessing field names. It still sends no reply, so the Siege screens keep waiting.
[Authenticated]
public sealed class MessagingAsyncRequestHandler : IRequestHandler<Messaging.Request>
{
	private const int MaxDepth = 4;
	private const int MaxBytesShown = 256;

	private readonly ILogger<MessagingAsyncRequestHandler>? _logger;

	public MessagingAsyncRequestHandler(ILogger<MessagingAsyncRequestHandler>? logger = null)
	{
		_logger = logger;
	}

	public void HandleRequest(SessionContext context, Messaging.Request request)
	{
		try
		{
			var dump = new StringBuilder();
			Describe(dump, "request", request, 0);
			_logger?.LogInformation("Siege: request from {user}\n{dump}", context.UserName, dump.ToString());
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Siege: failed to log request from {user}", context.UserName);
		}
	}

	// Writes one line per field, recursing into nested objects, lists and byte arrays.
	private static void Describe(StringBuilder dump, string name, object? value, int depth)
	{
		var indent = new string(' ', depth * 2);

		if (value is null)
		{
			dump.AppendLine($"{indent}{name} = null");
			return;
		}

		var type = value.GetType();

		if (value is byte[] bytes)
		{
			DescribeBytes(dump, indent, name, bytes, depth);
			return;
		}

		if (type.IsPrimitive || type.IsEnum || value is string or decimal or DateTime or Guid)
		{
			dump.AppendLine($"{indent}{name} = {value} ({type.Name})");
			return;
		}

		if (depth >= MaxDepth)
		{
			dump.AppendLine($"{indent}{name} = ({type.FullName}, too deep to show)");
			return;
		}

		if (value is IEnumerable items)
		{
			dump.AppendLine($"{indent}{name} ({type.Name}):");
			var index = 0;
			foreach (var item in items)
				Describe(dump, $"[{index++}]", item, depth + 1);
			return;
		}

		dump.AppendLine($"{indent}{name} ({type.FullName}):");
		const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

		foreach (var field in type.GetFields(flags))
			Describe(dump, field.Name, field.GetValue(value), depth + 1);

		foreach (var property in type.GetProperties(flags))
		{
			if (property.GetIndexParameters().Length > 0 || !property.CanRead)
				continue;
			Describe(dump, property.Name, property.GetValue(value), depth + 1);
		}
	}

	// A byte array could be the packed envelope, so show its raw bytes and try the two likeliest ways to read it.
	private static void DescribeBytes(StringBuilder dump, string indent, string name, byte[] bytes, int depth)
	{
		var shown = bytes.AsSpan(0, Math.Min(bytes.Length, MaxBytesShown));
		var more = bytes.Length > MaxBytesShown ? " ..." : "";
		dump.AppendLine($"{indent}{name} = byte[{bytes.Length}]");
		dump.AppendLine($"{indent}  hex:  {Convert.ToHexString(shown)}{more}");
		dump.AppendLine($"{indent}  text: {Encoding.UTF8.GetString(shown)}{more}");

		try
		{
			var decoded = EncData.Decode(bytes);
			Describe(dump, "as EncData", decoded, depth + 1);
		}
		catch (Exception ex)
		{
			dump.AppendLine($"{indent}  as EncData: not readable ({ex.GetType().Name})");
		}
	}
}
