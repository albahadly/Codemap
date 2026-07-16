namespace Codemap.Application.Messaging;

public interface INotification { }

public interface INotificationHandler<TNotification> where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken ct = default);
}
