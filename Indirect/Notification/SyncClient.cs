﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using DotNetty.Buffers;
using DotNetty.Codecs.Mqtt.Packets;
using InstaSharper.API;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Indirect.Notification
{
    class SyncClient
    {
        public event Action MessageReceived;  

        private readonly InstaApi _instaApi;
        public SyncClient(InstaApi api, Action messageReceivedHandler = null)
        {
            _instaApi = api;
            MessageReceived += messageReceivedHandler;
        }

        // Shutdown the client by stop pinging the server
        public void Shutdown() => _wsClientPinging.Cancel();

        public async void Start()
        {
            _wsClientPinging?.Cancel();
            _wsClientPinging = new CancellationTokenSource();
            _packetId = 1;
            var state = _instaApi.GetStateData();
            var device = _instaApi.DeviceInfo;
            var cookieHeader = state.Cookies.GetCookieHeader(new Uri("https://i.instagram.com"));
            var connectPacket = new ConnectPacket
            {
                CleanSession = true,
                ClientId = "mqttwsclient",
                HasUsername = true,
                HasPassword = false,
                KeepAliveInSeconds = 10,
                ProtocolLevel = 3,
                ProtocolName = "MQIsdp"
            };
            var aid = GenerateDigitsRandom(16);
            var userAgent =
                $"Mozilla/5.0 (Linux; Android 10; {device.DeviceName}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.93 Mobile Safari/537.36";
            var json = new JObject(
                new JProperty("u", _instaApi.UserSession.LoggedInUser.Pk),
                new JProperty("s", GenerateDigitsRandom(16)),
                new JProperty("cp", 1),
                new JProperty("ecp", 0),
                new JProperty("chat_on", true),
                new JProperty("fg", true),
                new JProperty("d", device.PhoneId.ToString()),
                new JProperty("ct", "cookie_auth"),
                new JProperty("mqtt_sid", ""),
                new JProperty("aid", aid),
                new JProperty("st", new JArray()),
                new JProperty("pm", new JArray()),
                new JProperty("dc", ""),
                new JProperty("no_auto_fg", true),
                new JProperty("a", userAgent)
            );
            var username = JsonConvert.SerializeObject(json, Formatting.None);
            connectPacket.Username = username;
            // var buffer = StandalonePacketEncoder.EncodePacket(connectPacket);
            var messageWebsocket = new MessageWebSocket();
            messageWebsocket.Control.MessageType = SocketMessageType.Binary;
            messageWebsocket.SetRequestHeader("Cookie", cookieHeader);
            messageWebsocket.SetRequestHeader("User-Agent", userAgent);
            messageWebsocket.SetRequestHeader("Origin", "https://www.instagram.com");
            messageWebsocket.MessageReceived += SyncClientOnMessageReceived;
            messageWebsocket.Closed += (sender, args) => Debug.WriteLine("wsclient closed");
            var buffer = StandalonePacketEncoder.EncodePacket(connectPacket);
            try
            {
                await messageWebsocket.ConnectAsync(new Uri("wss://edge-chat.instagram.com/chat"));
                await messageWebsocket.OutputStream.WriteAsync(buffer);
                await messageWebsocket.OutputStream.FlushAsync();
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
        }


        private int _packetId = 1;
        private CancellationTokenSource _wsClientPinging;
        private async void SyncClientOnMessageReceived(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            var dataReader = args.GetDataReader();
            var outStream = sender.OutputStream;
            var loggedInUser = _instaApi.UserSession.LoggedInUser;
            Packet packet;
            try
            {
                packet = StandalonePacketDecoder.DecodePacket(dataReader);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                return;
            }

            switch (packet.PacketType)
            {
                case PacketType.CONNACK:
                    Debug.WriteLine(packet.PacketType);

                    var subscribePacket = new SubscribePacket(
                        _packetId++,
                        new SubscriptionRequest("/ig_message_sync", QualityOfService.AtMostOnce),
                        new SubscriptionRequest("/ig_send_message_response", QualityOfService.AtMostOnce)
                    );
                    await WriteAndFlushPacketAsync(subscribePacket, outStream);

                    var unsubPacket = new UnsubscribePacket(_packetId++, "/ig_sub_iris_response");
                    await WriteAndFlushPacketAsync(unsubPacket, outStream);

                    subscribePacket = new SubscribePacket(_packetId++,
                        new SubscriptionRequest("/ig_sub_iris_response", QualityOfService.AtMostOnce));
                    await WriteAndFlushPacketAsync(subscribePacket, outStream);
                    var random = new Random();
                    var json = new JObject(
                        new JProperty("seq_id", 23936 + random.Next(-50, 50)),  // Or Iris will return Overflow?
                        new JProperty("snapshot_at_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                        new JProperty("snapshot_app_version", "web"),
                        new JProperty("subscription_type", "message"));
                    var jsonBytes = GetJsonBytes(json);
                    var irisPublishPacket = new PublishPacket(QualityOfService.AtLeastOnce, false, false)
                    {
                        PacketId = _packetId++,
                        TopicName = "/ig_sub_iris",
                        Payload = Unpooled.CopiedBuffer(jsonBytes)
                    };
                    await WriteAndFlushPacketAsync(irisPublishPacket, outStream);
                    json = new JObject(new JProperty("unsub", new JArray($"ig/u/v1/{loggedInUser.Pk}")));
                    jsonBytes = GetJsonBytes(json);
                    var pubsubPublishPacket = new PublishPacket(QualityOfService.AtLeastOnce, false, false)
                    {
                        PacketId = _packetId++,
                        TopicName = "/pubsub",
                        Payload = Unpooled.CopiedBuffer(jsonBytes)
                    };
                    await WriteAndFlushPacketAsync(pubsubPublishPacket, outStream);
                    unsubPacket = new UnsubscribePacket(_packetId++, "/pubsub");
                    await WriteAndFlushPacketAsync(unsubPacket, outStream);
                    subscribePacket = new SubscribePacket(_packetId++,
                        new SubscriptionRequest("/pubsub", QualityOfService.AtMostOnce));
                    await WriteAndFlushPacketAsync(subscribePacket, outStream);

                    json = new JObject(new JProperty("sub", new JArray($"ig/u/v1/{loggedInUser.Pk}")));
                    jsonBytes = GetJsonBytes(json);
                    pubsubPublishPacket = new PublishPacket(QualityOfService.AtLeastOnce, false, false)
                    {
                        PacketId = _packetId++,
                        TopicName = "/pubsub",
                        Payload = Unpooled.CopiedBuffer(jsonBytes)
                    };
                    await WriteAndFlushPacketAsync(pubsubPublishPacket, outStream);


                    Debug.WriteLine(packet.PacketType);
                    _ = Task.Run(async () =>
                    {
                        while (!_wsClientPinging.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(TimeSpan.FromSeconds(8), _wsClientPinging.Token);
                                var pingPacket = PingReqPacket.Instance;
                                var pingBuffer = StandalonePacketEncoder.EncodePacket(pingPacket);
                                await sender.OutputStream.WriteAsync(pingBuffer);
                                await sender.OutputStream.FlushAsync();
                            }
                            catch (TaskCanceledException)
                            {
                                Debug.WriteLine("Stopped pinging sync server");
                                return;
                            }
                        }
                    });
                    return;

                case PacketType.PUBLISH:
                    var publishPacket = (PublishPacket)packet;
                    var payload = publishPacket.Payload.ReadString(publishPacket.Payload.ReadableBytes, Encoding.UTF8);
                    if (publishPacket.TopicName == "/ig_message_sync") MessageReceived?.Invoke();
                    Debug.WriteLine($"wsclient pub to {publishPacket.TopicName} payload: {payload}");

                    if (publishPacket.QualityOfService == QualityOfService.AtLeastOnce)
                    {
                        await WriteAndFlushPacketAsync(PubAckPacket.InResponseTo(publishPacket), outStream);
                    }
                    return;

                default:
                    Debug.WriteLine(packet.PacketType);
                    break;
            }
        }

        private static async Task WriteAndFlushPacketAsync(Packet packet, IOutputStream outStream)
        {
            Debug.WriteLine($"Sending {packet.PacketType}");
            await outStream.WriteAsync(StandalonePacketEncoder.EncodePacket(packet));
            await outStream.FlushAsync();

            if (packet is PublishPacket publishPacket)
            {
                publishPacket.Payload.SetReaderIndex(0);
                var json = publishPacket.Payload.ReadString(publishPacket.Payload.ReadableBytes, Encoding.UTF8);
                Debug.WriteLine($"Payload: {json}");
                publishPacket.Payload?.Release();
            }
        }

        private static byte[] GetJsonBytes(JObject json)
        {
            var jsonString = JsonConvert.SerializeObject(json, Formatting.None);
            return Encoding.UTF8.GetBytes(jsonString);
        }

        // Generate random number without 0s
        private static ulong GenerateDigitsRandom(int length)
        {
            var result = "";
            var random = new Random();
            for (int i = 0; i < length; i++)
            {
                result += random.Next(1, 9).ToString();
            }

            return ulong.Parse(result);
        }
    }
}
