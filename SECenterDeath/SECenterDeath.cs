using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using SEModAPIExtensions.API;
using SEModAPIExtensions.API.Plugin;
using SEModAPIExtensions.API.Plugin.Events;
using SEModAPIInternal.API.Common;
using System.Runtime.InteropServices;
using System.Linq;
using VRage.ModAPI;
using VRageMath;
//using VRage.Game;
//using VRage.Game.Components;
//using VRage.Game.Entity;
//using Sandbox.Game.Entities;
using Sandbox.ModAPI;
//using Sandbox.Common;
//using System.IO;
//using System.Reflection;
//using Newtonsoft.Json;

namespace SECenterDeath
{
    public class Main : IPlugin, IChatEventHandler //IPlayerEventHandler, ICubeGridHandler, ICubeBlockEventHandler, ISectorEventHandler
    {
        #region "Properties"

        private Thread mainThread;
        private bool running;
        private Vector3 center;
        BoundingSphereD centerSphere;
        private int centerRadius = 100;
        private int captureInterval = 60;
        private int captureRemaining;
        private bool capturing = false;
        private long currentFactionCapturing;
        private Dictionary<long, int> capturePoints;
        private int leaderboardCount = 10;
        private int chatCharachterLength = 40;
        private string serverMessageLabel = "CenterDeath: ";

        [Category("SECenterDeath")]
        [Description("Center Radius (meters)")]
        [Browsable(true)]
        [ReadOnly(false)]
        public int CenterRadius
        {
            get { return centerRadius; }
            set { centerRadius = value; }
        }

        [Category("SECenterDeath")]
        [Description("Capture Interval (seconds)")]
        [Browsable(true)]
        [ReadOnly(false)]
        public int CaptureInterval
        {
            get { return captureInterval; }
            set { captureInterval = value; }
        }

        #endregion

        #region IPlugin

        public Guid Id
        {
            get
            {
                GuidAttribute guidAttr = (GuidAttribute)typeof(Main).Assembly.GetCustomAttributes(typeof(GuidAttribute), true)[0];
                return new Guid(guidAttr.Value);
            }
        }

        public string Name
        {
            get { return "SECenterDeath"; }
        }

        public Version Version
        {
            get { return typeof(Main).Assembly.GetName().Version; }
        }

        public void Init()
        {
            center = new Vector3(0, 0, 0);
            centerSphere = new BoundingSphereD(center, centerRadius);
            
            capturePoints = new Dictionary<long, int>();
            captureRemaining = captureInterval;
            Console.WriteLine("SECenterDeath plugin initialized!");

            running = true;
            mainThread = new Thread(main);
            mainThread.Start();
        }

        public void Update(){}

        public void Shutdown()
        {
            running = false;
            mainThread.Join(1000);
            mainThread.Abort();

            return;
        }

        #endregion

        #region IChatEventHandler

        public void OnChatSent(ChatManager.ChatEvent ce){}

        public void OnChatReceived(ChatManager.ChatEvent ce)
        {
            if (ce.SourceUserId == 0)
                return;

            if (ce.Message == "/leaderboard")
            {
                var leaders = capturePoints.OrderBy(key => key.Value).Take(leaderboardCount).ToList();

                String msg = "Top " + leaderboardCount + ":\n";
                int count = 1;
                foreach (var topFaction in leaders)
                {
                    IMyFaction faction = MyAPIGateway.Session.Factions.TryGetFactionById(topFaction.Key);
                    msg += count.ToString() + ". " + faction.Name + ": " + topFaction.Value + " captures\n";
                    count++;
                }
                ChatManager.Instance.SendPrivateChatMessage(ce.SourceUserId, msg);
                return;
            }

            if (PlayerManager.Instance.IsUserAdmin(ce.SourceUserId))
            {
                if (ce.Message == "/cdenable" && !running)
                {
                    Init();
                    ChatManager.Instance.SendPublicChatMessage("SECenterDeath enabled!");
                }

                if (ce.Message == "/cddisable" && running)
                {
                    Shutdown();
                    ChatManager.Instance.SendPublicChatMessage("SECenterDeath disabled!");
                }
            }
        }

        #endregion

        private void main()
        {
            while (running)
            {
                try
                {
                    Thread.Sleep(1000);

                    List<IMyIdentity> identities = new List<IMyIdentity>();
                    MyAPIGateway.Players.GetAllIdentites(identities);
                    List<IMyEntity> entitiesInSphere = MyAPIGateway.Entities.GetEntitiesInSphere(ref centerSphere);

                    Dictionary<long, int> insideCenter = new Dictionary<long, int>();
                    if (entitiesInSphere.Count == 0)
                    {
                        dethroneKing();
                    }
                    else
                    {
                        // Figure out who is inside the hill and what faction they are apart of, tally up faction members
                        foreach (IMyEntity entity in entitiesInSphere)
                        {
                            if (entity is IMyCharacter)
                            {
                                IMyIdentity identity = identities.FirstOrDefault(x => x.DisplayName == entity.DisplayName);
                                List<IMyPlayer> players = new List<IMyPlayer>();
                                MyAPIGateway.Players.GetPlayers(players, p => p.PlayerID == identity.PlayerId);

                                if (players.Count > 0)
                                {
                                    IMyPlayer player = players.First();
                                    Vector3 playerPosition = player.GetPosition();

                                    if (Distance(playerPosition, center) < centerRadius)
                                    {
                                        IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(player.PlayerID);

                                        if (faction != null)
                                        {
                                            if (insideCenter.ContainsKey(faction.FactionId))
                                            {
                                                insideCenter[faction.FactionId] = insideCenter[faction.FactionId] + 1;
                                            }
                                            else
                                            {
                                                insideCenter[faction.FactionId] = 1;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // Figure out if there a faction tie, or an only faction or highest member faction
                        if (insideCenter.Count == 0) // Current King dethroned - No one gets a point
                        {
                            dethroneKing();
                        }
                        else if (insideCenter.Count == 1) // Potentially new King
                        {
                            throneKing(insideCenter.First().Key);
                        }
                        else // Competition! - Figure out which faction is the best
                        {
                            var topTwoFactions = insideCenter.OrderBy(key => key.Value).Take(2).ToList();

                            if (topTwoFactions.ElementAt(0).Value > topTwoFactions.ElementAt(1).Value) // Potentially new King
                            {
                                throneKing(topTwoFactions.ElementAt(0).Key);
                            }
                            else // Tie - No King
                            {
                                dethroneKing();
                            }
                        }
                    }

                    if (capturing)
                    {
                        // Award Capture Point
                        if (captureRemaining <= 0)
                        {
                            if (capturePoints.ContainsKey(currentFactionCapturing))
                            {
                                capturePoints[currentFactionCapturing] = capturePoints[currentFactionCapturing] + 1;
                            }
                            else
                            {
                                capturePoints.Add(currentFactionCapturing, 1);
                            }

                            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetFactionById(currentFactionCapturing);
                            SendPublicChatMessage("*** " + faction.Name + " *** CAPTURED THE CENTER! - AWARDED a CAPTURE POINT! - Check /leaderboard");
                            resetCapturing();
                        }

                        if (captureRemaining < 300 && (captureRemaining % 50 == 0 || (captureRemaining <= 50 && captureRemaining % 10 == 0) || captureRemaining <= 10))
                        {
                            SendPublicChatMessage(captureRemaining + " seconds remaining");
                        }

                        captureRemaining--;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("SECenterDeath ERROR - Exception: " + e.ToString());
                    SendPublicChatMessage("ERROR - Plugin shutting down");
                    running = false;
                }
            }
        }

        private void throneKing(long newFactionCapturing)
        {
            capturing = true;

            if (currentFactionCapturing != newFactionCapturing) // New King!
            {
                currentFactionCapturing = newFactionCapturing;

                IMyFaction faction = MyAPIGateway.Session.Factions.TryGetFactionById(newFactionCapturing);
                SendPublicChatMessage("**** " + faction.Name + " **** is CAPTURING!");
            }
        }

        private void dethroneKing()
        {
            if (currentFactionCapturing != -1) // Remove King!
            {
                resetCapturing();
                SendPublicChatMessage("CAPTURE ABORTED!");
            }
        }

        private void resetCapturing()
        {
            currentFactionCapturing = -1;
            captureRemaining = captureInterval;
            capturing = false;
        }

        private double Distance(Vector3 a, Vector3D b)
        {
            return (
                        Math.Sqrt
                        (
                            Math.Pow(Math.Abs(a.X - b.X), 2) +
                            Math.Pow(Math.Abs(a.Y - b.Y), 2) +
                            Math.Pow(Math.Abs(a.Z - b.Z), 2)
                        )
                    );
        }

        private void SendPublicChatMessage(string msg)
        {
            int numSpaces = (chatCharachterLength - serverMessageLabel.Length - msg.Length);
            while (numSpaces <= 0){ numSpaces += chatCharachterLength; }
            string spaces = new String(' ', numSpaces);

            string finalMessage =
            "================================= " +
            serverMessageLabel + msg + spaces +
            "========================================";

            ChatManager.Instance.SendPublicChatMessage(finalMessage);
        }
    }
}

