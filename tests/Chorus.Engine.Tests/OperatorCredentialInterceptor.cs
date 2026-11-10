using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Chorus.Engine.Tests;

/// <summary>Attaches an operator bearer credential to every call a channel makes
/// (FIX-WEB-03). The admin host tests drove the control-plane Admin surface with a process
/// certificate; the browser Admin contract re-gates those handlers to the operator identity,
/// so the same call sites now present the operator credential — the interceptor keeps the
/// call sites unchanged while the channel they were built on gains the header.</summary>
public sealed class OperatorCredentialInterceptor(string token) : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var headers = new Metadata();
        if (context.Options.Headers is not null)
            foreach (var header in context.Options.Headers)
                headers.Add(header);
        headers.Add("authorization", "Bearer " + token);
        var options = context.Options.WithHeaders(headers);
        return continuation(request,
            new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, options));
    }
}
