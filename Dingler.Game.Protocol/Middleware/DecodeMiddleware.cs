extern alias HexGame;
using System.Text.Json;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Pipeline;
using Dingler.Game.Protocol.Chat;
using Dingler.Game.Protocol.Messages.Args;
using Dingler.Game.Protocol.Messages.Requests;
using HexGame::Game.Shared.Network;
using HexGame::Game.Shared.Utils;

namespace Dingler.Game.Protocol.Middleware;

public sealed class DecodeMiddleware : IMiddleware<RequestContext>
{
	public async Task InvokeAsync(RequestContext context, PipelineBuilder<RequestContext>.MiddlewareDelegate next,
		CancellationToken token)
	{
		if (!context.TryGetHeader(out var header))
		{
			throw new InvalidOperationException(
				$"Could not find header for request for session {context.SessionContext.SessionId}");
		}

		if (header.Target is not null && header.Target.Equals("newsession"))
		{
			context.RequestObject = new SessionCreationRequestEvent();
		}
		else if (header.Target is not null && header.Target.Equals("auth:req") && header.Instance!.Equals("auth:req"))
		{
			context.RequestObject = JsonSerializer.Deserialize<AuthenticationRequestArg>(context.RawRequest) ??
			                        throw new Exception("Malformed authentication payload");
		}
		else if (header.Instance is not null && header.Instance.ToLower().Equals("ping"))
		{
			context.RequestObject = new PingRequestArg();
		}
		else if (header.Instance is not null && header.Instance.ToLower().Equals("chat"))
		{
			var rawChatRequest = JsonSerializer.Deserialize<RawChatRequest>(context.RawRequest) ??
			                     throw new Exception("Malformed chat payload");

			if (rawChatRequest.Action is null)
				throw new Exception("Malformed chat payload: Action cannot be null");

			if (rawChatRequest.Room is null)
				throw new Exception("Malformed chat payload: Room cannot be null");
			
			rawChatRequest.User = context.SessionContext.UserName!;
			
			context.RequestObject = ChatMessageConverter.ParseChatRequest(rawChatRequest);

			if (context.RequestObject is null)
				context.RequestObject = rawChatRequest;
		}
		else
		{
			try
			{
				var wrapper = EncData.Decode<DataWrapper>(context.RawRequest);

				context.SetDataType(wrapper.DataType);
				context.SetRequestId(wrapper.RequestId);

				if (wrapper.Comp == 1)
				{
					wrapper.Bytes = Compressor.Decompress(wrapper.Bytes);
				}

				context.RequestObject = EncData.Decode(wrapper.Bytes);
			}
			catch (Exception ex) when (ex is not InvalidOperationException)
			{
				// A message we can't read used to end the player's connection. Now it's skipped and described in the log
				// (DinglerClient logs InvalidOperationException and goes on with the next message).
				var raw = context.RawRequest;
				var start = Convert.ToHexString(raw, 0, Math.Min(raw.Length, 48));
				throw new InvalidOperationException(
					$"Skipped a message that couldn't be decoded ({ex.GetType().Name}: {ex.Message}). Header {header}; " +
					$"body {raw.Length} bytes starting {start}");
			}
		}

		await next(context, token)
			.ConfigureAwait(false);
	}
}