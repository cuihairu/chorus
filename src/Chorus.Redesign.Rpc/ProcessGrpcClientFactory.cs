using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;

namespace Chorus.Redesign.Rpc;

public static class ProcessGrpcClientFactory
{
    public static GrpcChannel Create(Uri endpoint, X509Certificate2 clientCertificate,
        X509Certificate2 root, X509Certificate2 nodeAuthority,
        string clusterId, string expectedServerRole)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Internal gRPC requires HTTPS.", nameof(endpoint));
        var handler = new HttpClientHandler { ClientCertificateOptions = ClientCertificateOption.Manual };
        handler.ClientCertificates.Add(clientCertificate);
        handler.ServerCertificateCustomValidationCallback = (_, serverCertificate, _, _) =>
        {
            if (serverCertificate is null) return false;
            var identity = ProcessCertificateValidator.Validate(serverCertificate, root, clusterId, [nodeAuthority]);
            return identity is not null && identity.Role == expectedServerRole && identity.TenantId == "@cluster";
        };
        return GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = handler });
    }
}
