using Microsoft.Extensions.DependencyInjection;
using PackCall.Core.Sending;

namespace PackCall.Infrastructure.Sending;

internal sealed class KeyedChannelOperatorAvailability(IServiceProviderIsKeyedService services)
    : IChannelOperatorAvailability
{
    public bool HasOperator(string channel) => services.IsKeyedService(typeof(IMessageSender), channel);
}
