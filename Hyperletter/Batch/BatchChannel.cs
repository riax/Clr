using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Timers;
using Hyperletter.Channel;
using Hyperletter.EventArgs.Letter;
using Hyperletter.Extension;
using Hyperletter.Letter;

namespace Hyperletter.Batch {
    internal class BatchChannel : IChannel {
        private readonly BatchLetterBuilder _batchBuilder;
        private readonly IChannel _channel;
        private readonly BatchOptions _options;
        private readonly ConcurrentQueue<ILetter> _queue = new ConcurrentQueue<ILetter>();

        private readonly Timer _slidingTimeoutTimer;

        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly object _syncRoot = new object();
        private bool _sentBatch;

        public BatchChannel(SocketOptions options, IChannel channel, BatchLetterBuilder batchBuilder) {
            _channel = channel;
            _options = options.Batch;
            _batchBuilder = batchBuilder;

            _channel.ChannelConnected += abstractChannel => ChannelConnected(this);
            _channel.ChannelDisconnected += ChannelOnDisconnected;
            _channel.ChannelQueueEmpty += abstractChannel => {
                                              /* NOOP */
                                          };
            _channel.ChannelInitialized += ChannelOnInitialized;
            _channel.ChannelConnecting += abstractChannel => ChannelConnecting(this);
            _channel.ChannelDisconnecting += (abstractChannel, reason) => ChannelDisconnecting(this, reason);

            _channel.Received += ChannelOnReceived;
            _channel.Sent += ChannelOnSent;
            _channel.FailedToSend += ChannelOnFailedToSend;

            _slidingTimeoutTimer = new Timer(_options.Extend.TotalMilliseconds) {AutoReset = false};
            _slidingTimeoutTimer.Elapsed += SlidingTimeoutTimerOnElapsed;
        }

        public bool CanSend {
            get { return _channel.CanSend; }
        }

        public bool IsConnected {
            get { return _channel.IsConnected; }
        }

        public bool ShutdownRequested {
            get { return _channel.ShutdownRequested; }
        }

        public Guid RemoteNodeId {
            get { return _channel.RemoteNodeId; }
        }

        public Binding Binding {
            get { return _channel.Binding; }
        }

        public Direction Direction {
            get { return _channel.Direction; }
        }

        public event Action<IChannel> ChannelConnected;
        public event Action<IChannel> ChannelConnecting;
        public event Action<IChannel, ShutdownReason> ChannelDisconnected;
        public event Action<IChannel, ShutdownReason> ChannelDisconnecting;
        public event Action<IChannel> ChannelQueueEmpty;
        public event Action<IChannel> ChannelInitialized;

        public event Action<ILetter, ReceivedEventArgs> Received;
        public event Action<IChannel, ILetter> Sent;
        public event Action<IChannel, ILetter> FailedToSend;

        public void Initialize() {
            _channel.Initialize();
        }

        public EnqueueResult Enqueue(ILetter letter) {
            ChangeTimerState(false);
            ChangeTimerState(true);

            if(!_sentBatch)
                _stopwatch.Restart();

            _queue.Enqueue(letter);
            _batchBuilder.Add(letter);

            return TrySendBatch(false);
        }

        public void Heartbeat() {
            _channel.Heartbeat();
        }

        public void Disconnect() {
            _channel.Disconnect();
        }

        private void ChannelOnInitialized(IChannel channel) {
            ChannelInitialized(this);
        }

        private void ChannelOnDisconnected(IChannel channel, ShutdownReason reason) {
            ChangeTimerState(false);
            _sentBatch = false;
            FailedQueuedLetters();
            ChannelDisconnected(this, reason);
        }

        private void SlidingTimeoutTimerOnElapsed(object sender, ElapsedEventArgs elapsedEventArgs) {
            ChangeTimerState(false);
            TrySendBatch(true);
        }

        private void ChangeTimerState(bool enabled) {
            lock(_slidingTimeoutTimer) {
                _slidingTimeoutTimer.Enabled = enabled;
            }
        }

        private EnqueueResult TrySendBatch(bool timeout) {
            lock(_syncRoot) {
                if(_sentBatch)
                    return EnqueueResult.CantEnqueueMore;

                if(!_batchBuilder.IsEmpty && (timeout || _batchBuilder.IsFull || _stopwatch.ElapsedMilliseconds >= _options.MaxExtend.TotalMilliseconds)) {
                    _sentBatch = true;
                    _channel.Enqueue(_batchBuilder.Build());

                    return EnqueueResult.CantEnqueueMore;
                }

                return EnqueueResult.CanEnqueueMore;
            }
        }

        private void ChannelOnSent(IChannel channel, ILetter letter) {
            if(letter.Type == LetterType.Batch) {
                _sentBatch = false;

                for(int i = 0; i < letter.Parts.Length; i++)
                    Sent(this, _queue.Dequeue());
            } else
                Sent(this, _queue.Dequeue());

            ChannelQueueEmpty(this);
            TrySendBatch(false);
        }

        private void ChannelOnReceived(ILetter letter, ReceivedEventArgs receivedEventArgs) {
            Received(letter, receivedEventArgs);
        }

        private void ChannelOnFailedToSend(IChannel channel, ILetter letter) {
            _sentBatch = false;

            if(letter.Type == LetterType.Batch)
                FailedQueuedLetters();
            else
                FailedToSend(this, letter);
        }

        private void FailedQueuedLetters() {
            ILetter letter;
            while(_queue.TryDequeue(out letter)) {
                FailedToSend(this, letter);
            }
            _batchBuilder.Clear();
        }
    }
}