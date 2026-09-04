namespace ReminNote.Core.Reminders.Notifications;

/// <summary>
/// Combines host-owned catalogs (for example Main Windows surfaces and the
/// Widget surface) into the single catalog injected into Agent dispatch. It
/// performs no persistence and rejects duplicate channel ownership.
/// </summary>
public sealed class CompositeNotificationChannelCatalog : INotificationChannelCatalogSnapshot
{
    private readonly IReadOnlyDictionary<NotificationChannelId, INotificationChannel> channels;

    public CompositeNotificationChannelCatalog(IEnumerable<INotificationChannelCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var map = new Dictionary<NotificationChannelId, INotificationChannel>();
        foreach (var catalog in catalogs)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            var candidates = catalog is INotificationChannelCatalogSnapshot snapshot
                ? snapshot.ChannelIds.Select(id => catalog.TryGet(id, out var channel) ? channel : null)
                : NotificationChannels.P3.Select(id => catalog.TryGet(id, out var channel) ? channel : null);
            foreach (var channel in candidates)
            {
                if (channel is null)
                {
                    continue;
                }

                var channelId = NotificationChannels.RequireKnown(channel.ChannelId);
                if (!map.TryAdd(channelId, channel))
                {
                    throw NotificationContractException.Invalid(
                        NotificationErrorCodes.SerializationInvalid,
                        $"Channel '{channelId.Value}' was registered more than once.",
                        nameof(catalogs));
                }
            }
        }

        channels = new System.Collections.ObjectModel.ReadOnlyDictionary<NotificationChannelId, INotificationChannel>(map);
    }

    public IReadOnlyCollection<NotificationChannelId> ChannelIds => channels.Keys.ToArray();

    public bool TryGet(NotificationChannelId channelId, out INotificationChannel channel) =>
        channels.TryGetValue(channelId, out channel!);
}
