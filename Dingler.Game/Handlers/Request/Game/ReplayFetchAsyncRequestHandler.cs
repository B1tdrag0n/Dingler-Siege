extern alias HexGame;
using System.Text;
using System.Text.Json;
using Dingler.Data.Repositories;
using Dingler.Game.Replays;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Shared.GameReq;
using HexGame::Game.Shared.Network;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Game;

// Replay download, DataType 160000 (ServiceGameSession). The client's GameReplay.Download asks for a replay in pieces:
// a GameReq.Request whose Envelope is the JSON {"action":"replayfetch","Session":<ReplayWire.Key>,"Offset":n,"Size":16384}.
// Each reply is a GameReq.Response whose Envelope is that piece plus a 5-byte trailer (1 = more to come, then the file's
// total size); the client asks again from the next offset until the trailer says there is no more.
[Authenticated]
public sealed class ReplayFetchAsyncRequestHandler : IAsyncRequestHandler<GameReq.Request, IRequestArgs>
{
	// Neverness's cap. The client asks for 16 KB at a time.
	private const int MaxPiece = 1024 * 1024;

	private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

	private readonly ReplayRepository _replays;
	private readonly ILogger<ReplayFetchAsyncRequestHandler>? _logger;

	public ReplayFetchAsyncRequestHandler(ReplayRepository replays, ILogger<ReplayFetchAsyncRequestHandler>? logger = null)
	{
		_replays = replays;
		_logger = logger;
	}

	public async Task<IRequestArgs> HandleRequestAsync(SessionContext context, GameReq.Request request,
		CancellationToken token)
	{
		try
		{
			var json = Encoding.UTF8.GetString(request.Envelope ?? []);
			var fetch = JsonSerializer.Deserialize<FetchRequest>(json, JsonOptions);
			if (fetch?.action != GameReq.ReqAction.ReplayFetch)
				return Refuse(context, $"Unknown game request: {json}");
			if (!ReplayWire.TryParseKey(fetch.Session, out var replayId))
				return Refuse(context, $"No replay named '{fetch.Session}'.");

			var file = await _replays.GetFileAsync(replayId);
			if (file is null)
				return Refuse(context, $"Replay {replayId} isn't available.");
			if (fetch.Offset < 0 || fetch.Offset > file.Length)
				return Refuse(context, $"Replay {replayId} has no byte {fetch.Offset}.");

			var length = Math.Min(Math.Clamp(fetch.Size, 1, MaxPiece), file.Length - fetch.Offset);
			var more = fetch.Offset + length < file.Length;
			// Our own copy of the piece: given the whole file, EncodeReply can mistake it for a reply buffer and send
			// the wrong bytes (when the file is exactly 5 bytes longer than the piece).
			var piece = file.AsSpan(fetch.Offset, length).ToArray();

			if (fetch.Offset == 0)
				_logger?.LogInformation("Replay: {user} is downloading replay {replay} ({bytes} bytes)", context.UserName,
					replayId, file.Length);
			else
				_logger?.LogDebug("Replay: sending {user} bytes {from}-{to} of replay {replay}", context.UserName,
					fetch.Offset, fetch.Offset + length, replayId);
			if (!more)
				_logger?.LogInformation("Replay: {user} has all of replay {replay}", context.UserName, replayId);

			return new GameReq.Response
			{
				Envelope = GameReq.ReplayFetchResp.EncodeReply(piece, 0, length, more, file.Length)
			};
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Replay: failed to answer a download request from {user}", context.UserName);
			return new ErrorArgs { Error = 1, ErrorMessage = "The replay couldn't be sent. Please try again." };
		}
	}

	private ErrorArgs Refuse(SessionContext context, string message)
	{
		_logger?.LogInformation("Replay: refused a download for {user}: {reason}", context.UserName, message);
		return new ErrorArgs { Error = 1, ErrorMessage = message };
	}

	// The client's GameReq.ReplayFetchReq, field for field.
	private sealed class FetchRequest
	{
		public string? action;
		public string? Session;
		public int Offset;
		public int Size;
	}
}
