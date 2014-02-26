﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="Program.cs" company="Business Systems (UK) Ltd">
//   (C) Business Systems (UK) Ltd
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace NEventSocket.Example
{
    using System;
    using System.Reactive.Linq;
    using System.Security;
    using System.Threading.Tasks;

    using Common.Logging;
    using Common.Logging.Simple;

    using NEventSocket.FreeSwitch;
    using NEventSocket.FreeSwitch.Api;
    using NEventSocket.FreeSwitch.Applications;
    using NEventSocket.Util;

    /// <summary>The program.</summary>
    internal class Program
    {
        private static void Main(string[] args)
        {
            // set logger factory
            LogManager.Adapter = new ConsoleOutLoggerFactoryAdapter(
                LogLevel.All, true, true, true, "yyyy-MM-dd hh:mm:ss");

            Console.WriteLine("Starting...");

            OutboundSocketTest();
            InboundSocketTest();

            Console.WriteLine("Press [Enter] to exit.");
            Console.ReadLine();
        }

        private static async void InboundSocketTest()
        {
            var client = await InboundSocket.Connect("localhost", 8021, "ClueCon");
            Console.WriteLine("Authenticated!");

            await client.SubscribeEvents(EventType.DTMF);

            var originate =
                await
                client.Originate(
                    Sofia.User("1001"), 
                    new OriginateOptions
                        {
                            CallerIdNumber = "123456789", 
                            CallerIdName = "Dan Leg A", 
                            HangupAfterBridge = false,
                            Timeout = 20
                        });

            if (!originate.Success)
            {
                using (Colour.Use(ConsoleColor.DarkRed))
                {
                    Console.WriteLine("Originate Failed {0}", originate.HangupCause);
                    client.Exit();
                }
            }
            else
            {
                var uuid = originate.ChannelData.Headers[HeaderNames.CallerUniqueId];
                Console.WriteLine("Originate success {0}", originate.ChannelData.Headers[HeaderNames.AnswerState]);

                var recordingPath = "c:/temp/recording_{0}.wav".Fmt(uuid);

                client.OnHangup(uuid,
                          e =>
                              {
                                  using (Colour.Use(ConsoleColor.Red))
                                  {
                                      Console.WriteLine("Hangup Detected on A-Leg {0} {1}", e.Headers[HeaderNames.CallerUniqueId], e.Headers[HeaderNames.HangupCause]);
                                  }

                                  client.Exit();
                              });

                var playResult = await client.Play(uuid, "ivr/8000/ivr-call_being_transferred.wav");
                if (playResult.Success) Console.WriteLine("Played ok!");

                var bridge =
                    await
                    client.Bridge(
                        uuid, 
                        Sofia.User("1000"), 
                        new BridgeOptions()
                            {
                                Timeout = 10, 
                                CallerIdName = "Click2Dial", 
                                CallerIdNumber = "Click2Dial", 
                                HangupAfterBridge = false, 
                                IgnoreEarlyMedia = true, 
                                ContinueOnFail = true, 
                                RingBack = "${uk-ring}"
                            });

                if (!bridge.Success)
                {
                    using (Colour.Use(ConsoleColor.Red))
                    {
                        Console.WriteLine("Bridge failed {0}",  bridge.ResponseText);
                    }

                    await client.Play(uuid, "ivr/8000/ivr-call_rejected.wav");
                    await client.Hangup(uuid, "CALL_REJECTED");
                }
                else
                {
                    using (Colour.Use(ConsoleColor.Green))
                    {
                        Console.WriteLine("Bridge succeeded from {0} to {1} - {2}", bridge.ChannelData.UUID, bridge.BridgeUUID, bridge.ResponseText);
                    }

                    //when b-leg hangs up, play a notification to a-leg
                    client.OnHangup(bridge.BridgeUUID,
                                      async e =>
                                          {
                                              using (Colour.Use(ConsoleColor.Red))
                                              {
                                                  Console.WriteLine(
                                                      "Hangup Detected on B-Leg {0} {1}", 
                                                      e.Headers[HeaderNames.CallerUniqueId], 
                                                      e.Headers[HeaderNames.HangupCause]);
                                              }

                                              await client.Play(uuid, "ivr/8000/ivr-you_may_exit_by_hanging_up.wav");
                                          });

                    await client.SetChannelVariable(uuid, "RECORD_ARTIST", "'Opex Hosting Ltd'");
                    await client.SetChannelVariable(uuid, "RECORD_MIN_SEC", 0);
                    await client.SetChannelVariable(uuid, "RECORD_STEREO", "true");

                    var recordingResult = await client.Api("uuid_record {0} start {1}".Fmt(uuid, recordingPath));
                    Console.WriteLine("Recording... " + recordingResult.Success);

                    if (recordingResult.Success)
                    {
                        client.Events.Where(x => x.UUID == uuid && x.EventType == EventType.DTMF)
                            .Subscribe(async (e) =>
                                            {
                                                var dtmf = e.Headers[HeaderNames.DTMFDigit];
                                                switch (dtmf)
                                                {
                                                    case "1":
                                                        Console.WriteLine("Mask recording");
                                                        await client.Api("uuid_record {0} mask {1}".Fmt(uuid, recordingPath));
                                                        await client.ExecuteAppAsync(uuid, "displace_session", "{0}".Fmt("ivr/8000/ivr-recording_paused.wav"));
                                                        break;
                                                    case "2":
                                                        Console.WriteLine("Unmask recording");
                                                        await client.Api("uuid_record {0} unmask {1}".Fmt(uuid, recordingPath));
                                                        await client.ExecuteAppAsync(uuid, "displace_session", "{0}".Fmt("ivr/8000/ivr-begin_recording.wav"));
                                                        break;
                                                    case "3":
                                                        Console.WriteLine("Stop recording");
                                                        await client.Api("uuid_record {0} stop {1}".Fmt(uuid, recordingPath));
                                                        await client.ExecuteAppAsync(uuid, "displace_session", "{0}".Fmt("ivr/8000/ivr-recording_stopped.wav"));
                                                        break;
                                                }
                                            });
                    }
                }
            }
        }

        private static void OutboundSocketTest()
        {
            var listener = new OutboundListener(8084);

            listener.Connections.Subscribe(
                async connection =>
                    {
                        Console.WriteLine("New Socket connected");

                        connection.Events.Where(x => x.EventType == EventType.CHANNEL_HANGUP).Take(1).Subscribe(
                            e =>
                                {
                                    using (Colour.Use(ConsoleColor.Red))
                                    {
                                        Console.WriteLine(
                                            "HANGUP DETECTED {0} {1}", 
                                            e.Headers[HeaderNames.CallerUniqueId], 
                                            e.Headers[HeaderNames.HangupCause]);
                                    }

                                    connection.Exit();
                                });

                        var uuid = connection.ChannelData.Headers[HeaderNames.UniqueId];
                        Console.WriteLine(uuid);

                        await
                            connection.SubscribeEvents(
                                EventType.PLAYBACK_START, 
                                EventType.PLAYBACK_STOP, 
                                EventType.DTMF, 
                                EventType.CHANNEL_HANGUP_COMPLETE, 
                                EventType.CHANNEL_HANGUP);

                        await connection.Linger();
                        await connection.SendMessage(uuid, "call-command: execute\nexecute-app-name: answer");

                        var result =
                            await
                            connection.Play(
                                uuid, 
                                "$${base_dir}/sounds/en/us/callie/misc/8000/misc-freeswitch_is_state_of_the_art.wav");
                        Console.WriteLine("Playback : {0}", result.Success);

                        if (result.ChannelData.AnswerState != AnswerState.Hangup) await connection.Hangup(uuid, "NORMAL_CLEARING");
                    });

            listener.Start();
        }
    }
}