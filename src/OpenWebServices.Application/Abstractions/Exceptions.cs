namespace OpenWebServices.Application.Abstractions;

public sealed class NotFoundException(string what) : Exception($"{what} was not found.");

public sealed class ProviderNotConfiguredException(string providerId)
    : Exception($"No API token is configured for provider '{providerId}'. Add one under Settings.");

/// <summary>A hosting provider rejected a request or could not be reached.</summary>
public sealed class ProviderException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class OfferUnavailableException(string offer)
    : Exception($"The selected offer '{offer}' does not exist or is not available right now.");
