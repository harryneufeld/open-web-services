using OpenWebServices.Domain.Providers;

namespace OpenWebServices.Application.Abstractions;

public sealed class NotFoundException(string what) : Exception($"{what} was not found.");

public sealed class ProviderNotConfiguredException(string groupName)
    : Exception($"The provider group '{groupName}' has no usable credentials. Update them under Groups.");

public sealed class CapabilityNotSupportedException(string provider, ResourceKind kind)
    : Exception($"{provider} does not support {ResourceKinds.Label(kind)}.");

/// <summary>A provider supports a resource kind but not one of its features, e.g. firewalls for its servers.</summary>
public sealed class FeatureNotSupportedException(string provider, string feature)
    : Exception($"ows does not support {feature} at {provider} yet.");

/// <summary>A hosting provider rejected a request or could not be reached.</summary>
public sealed class ProviderException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class OfferUnavailableException(string offer)
    : Exception($"The selected offer '{offer}' does not exist or is not available right now.");

/// <summary>
/// A server presented a different SSH host key than the one pinned on first contact. Either the server was
/// reinstalled, or someone is intercepting the connection; ows refuses to send anything until a user decides.
/// </summary>
public sealed class HostKeyMismatchException(string host, string expected, string actual)
    : Exception($"{host} presented the host key {actual}, but ows pinned {expected}. If the server was reinstalled, " +
                "trust the new host key under SSH access; otherwise someone may be intercepting the connection.");

/// <summary>ows could not reach or sign in to a server over SSH.</summary>
public sealed class RemoteAccessException(string message, Exception? inner = null) : Exception(message, inner);
