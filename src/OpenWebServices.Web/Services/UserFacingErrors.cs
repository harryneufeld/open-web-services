using OpenWebServices.Application.Abstractions;
using OpenWebServices.Domain;

namespace OpenWebServices.Web.Services;

/// <summary>Exceptions whose message is safe and useful to show to the user as-is.</summary>
public static class UserFacingErrors
{
    public static bool Matches(Exception ex) => ex is
        DomainException or NotFoundException or ProviderException
        or ProviderNotConfiguredException or OfferUnavailableException or CapabilityNotSupportedException
        or FeatureNotSupportedException or RemoteAccessException or HostKeyMismatchException or ForbiddenException;
}
