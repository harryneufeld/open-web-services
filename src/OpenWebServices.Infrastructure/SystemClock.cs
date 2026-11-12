using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure;

internal sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
