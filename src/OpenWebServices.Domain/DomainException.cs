namespace OpenWebServices.Domain;

/// <summary>Raised when a business rule or invariant of the domain is violated.</summary>
public sealed class DomainException(string message) : Exception(message);
