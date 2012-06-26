using System;
using System.Collections.Concurrent;
using Hyperletter.Abstraction;
using Hyperletter.Core.Extension;

namespace Hyperletter.Core {
    public class UnicastSocket : AbstractHyperSocket {
        public event Action<ILetter> Requeued;

        private readonly ConcurrentQueue<IAbstractChannel> _channelQueue = new ConcurrentQueue<IAbstractChannel>();
        private readonly ConcurrentQueue<ILetter> _sendQueue = new ConcurrentQueue<ILetter>();
        private readonly ConcurrentQueue<ILetter> _prioritySendQueue = new ConcurrentQueue<ILetter>();

        private readonly object _syncRoot = new object();

        protected override void ChannelFailedToSend(IAbstractChannel abstractChannel, ILetter letter) {
            if (letter.Options.IsSet(LetterOptions.Requeue)) {
                _prioritySendQueue.Enqueue(letter);
                TrySend();
                if (Requeued != null)
                    Requeued(letter);
            } else {
                Discard(abstractChannel, letter);
            }
        }

        protected override IAbstractChannel PrepareChannel(IAbstractChannel channel) {
            channel.ChannelQueueEmpty += ChannelCanSend;
            channel.ChannelInitialized += ChannelCanSend;
            return channel;
        }

        private void ChannelCanSend(IAbstractChannel abstractChannel) {
            ILetter letter;
            if (_sendQueue.TryDequeue(out letter)) {
                abstractChannel.Enqueue(letter);
            } else {
                _channelQueue.Enqueue(abstractChannel);
                TrySend();
            }
        }

        public override void Send(ILetter letter) {
            _sendQueue.Enqueue(letter);
            TrySend();
        }
       
        protected void TrySend() {
            lock (_syncRoot) {
                while (CanSend()) {
                    IAbstractChannel channel = GetNextChannel();
                    ILetter letter = GetNextLetter();
                    channel.Enqueue(letter);
                }
            }
        }

        private bool CanSend() {
            return _channelQueue.Count > 0 && (_prioritySendQueue.Count > 0 || _sendQueue.Count > 0);
        }

        private IAbstractChannel GetNextChannel() {
            IAbstractChannel channel;
            _channelQueue.TryDequeue(out channel);
            return channel;
        }

        private ILetter GetNextLetter() {
            ILetter letter;
            if (!_prioritySendQueue.TryDequeue(out letter))
                _sendQueue.TryDequeue(out letter);
            return letter;
        }
    }
}