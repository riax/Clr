using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using Hyperletter.Batch;
using Hyperletter.Channel;
using Hyperletter.Extension;
using Hyperletter.Letter;

namespace Hyperletter {
    public class HyperSocket : IDisposable, IHyperSocket {
        private readonly ConcurrentDictionary<Binding, SocketListener> _listeners = new ConcurrentDictionary<Binding, SocketListener>();
        private readonly ConcurrentDictionary<Binding, IChannel> _channels = new ConcurrentDictionary<Binding, IChannel>();
        private readonly ConcurrentDictionary<Guid, IChannel> _routeChannels = new ConcurrentDictionary<Guid, IChannel>();

        private readonly LetterDispatcher _letterDispatcher;

        internal LetterSerializer LetterSerializer { get; private set; }

        public SocketOptions Options { get; private set; }
        public IEnumerable<IChannel> Channels { get { return _channels.Values; } }

        public event Action<IHyperSocket, ILetter> Sent;
        public event Action<IHyperSocket, ILetter> Received;
        public event Action<IHyperSocket, Binding, ILetter> Discarded;
        public event Action<ILetter> Requeued;

        public event Action<IHyperSocket, Binding> Connected;
        public event Action<IHyperSocket, Binding> Disconnected;

        public HyperSocket() : this(new SocketOptions()) {
        }

        public HyperSocket(SocketOptions options) {
            Options = options;
            LetterSerializer = new LetterSerializer(options.Id);

            _letterDispatcher = new LetterDispatcher(this);
        }

        public void Bind(IPAddress ipAddress, int port) {
            var binding = new Binding(ipAddress, port);

            var listener = new SocketListener(this, binding);
            _listeners[binding] = listener;
            listener.IncomingChannel += HookupChannel;
            listener.Start();
        }

        public void Connect(IPAddress ipAddress, int port) {
            var bindingKey = new Binding(ipAddress, port);
            var channel = new OutboundChannel(this, bindingKey);
            HookupChannel(channel);
            channel.Connect();
        }

        public void Send(ILetter letter) {
            _letterDispatcher.EnqueueLetter(letter);
        }

        public void Answer(ILetter answer, ILetter answeringTo) {
            Guid address = answeringTo.Address[0];

            IChannel channel;
            if(_routeChannels.TryGetValue(address, out channel)) {
                channel.Enqueue(answer);
            }
        }

        public void Dispose() {
            foreach(var listener in _listeners.Values)
                listener.Dispose();

            foreach(var channel in _channels.Values)
                channel.Dispose();
        }

        private void HookupChannel(IChannel channel) {
            if(Options.Batch.Enabled)
                channel = new BatchChannel(this, channel);

            channel.Received += ChannelReceived;
            channel.FailedToSend += ChannelFailedToSend;
            channel.Sent += ChannelSent;
            channel.ChannelDisconnected += ChannelDisconnected;
            channel.ChannelConnected += ChannelConnected;
            channel.ChannelInitialized += ChannelInitialized;
            channel.ChannelQueueEmpty += ChannelAvailable;
            channel.ChannelInitialized += ChannelAvailable;

            _channels[channel.Binding] = channel;
            channel.Initialize();
        }

        private void ChannelInitialized(IChannel obj) {
            _routeChannels.TryAdd(obj.ConnectedTo, obj);
        }

        private void ChannelConnected(IChannel obj) {
            if(Connected != null)
                Connected(this, obj.Binding);
        }

        private void ChannelDisconnected(IChannel channel) {
            channel.Dispose();

            var binding = channel.Binding;
            _channels.Remove(binding);
            _routeChannels.Remove(channel.ConnectedTo);

            if(Disconnected != null)
                Disconnected(this, binding);

            if(channel.Direction == Direction.Outbound)
                Connect(binding.IpAddress, binding.Port);
        }

        private void ChannelReceived(IChannel channel, ILetter letter) {
            if(Received == null)
                return;

            if(letter.Type == LetterType.Batch) {
                for(int i = 0; i < letter.Parts.Length; i++)
                    Received(this, LetterSerializer.Deserialize(letter.Parts[i]));
            } else {
                Received(this, letter);
            }
        }

        private void ChannelSent(IChannel channel, ILetter letter) {
            if(Sent != null) Sent(this, letter);
        }

        private void ChannelFailedToSend(IChannel channel, ILetter letter) {
            if(letter.Options.HasFlag(LetterOptions.Multicast)) {
                Discard(channel, letter);
            } else if(letter.Options.HasFlag(LetterOptions.Requeue)) {
                Requeue(letter);
            } else {
                Discard(channel, letter);
            }
        }

        private void Discard(IChannel channel, ILetter letter) {
            if(Discarded != null && !letter.Options.HasFlag(LetterOptions.SilentDiscard))
                Discarded(this, channel.Binding, letter);
        }

        private void Requeue(ILetter letter) {
            _letterDispatcher.EnqueueLetter(letter);
            if(Requeued != null)
                Requeued(letter);
        }

        private void ChannelAvailable(IChannel channel) {
            _letterDispatcher.EnqueueChannel(channel);
        }
    }
}