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
