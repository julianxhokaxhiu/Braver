﻿// This program and the accompanying materials are made available under the terms of the
//  Eclipse Public License v2.0 which accompanies this distribution, and is available at
//  https://www.eclipse.org/legal/epl-v20.html
//  
//  SPDX-License-Identifier: EPL-2.0

using LiteNetLib.Utils;
using Microsoft.VisualBasic;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Braver.Net {

    public enum MessageType {
        Unknown = 0,

        ScreenReady = 1,
        PopScreen = 2,
        Transition = 3,

        FieldScreen = 100,
        FieldModel = 101,
        FieldBG = 102,        
        FieldEntityModel = 103,
        FieldBGScroll = 104,

        UIScreen = 200,
        UIState = 201,

        BattleScreen = 300,
        AddBattleModel = 301,
        SetBattleCamera = 302,
        BattleCharacterReady = 303,
        TargetOptions = 304,

        CycleBattleMenu = 350,
        GetTargetOptions = 351,
        QueueAction = 352,

        SwirlScreen = 390,

        ConnectedMessage = 8000,

        SfxMessage = 9001,
        MusicMessage = 9002,
        MusicVolumeMessage = 9003,
        SfxChannelMessage = 9004,
    }

    public interface IListen<T> where T : ServerMessage {
        void Received(T message);
    }

    public interface IClientListen<T> where T : ClientMessage {
        void Received(T message, Guid playerID);
    }

    public abstract class Net {

        private static Dictionary<MessageType, Func<NetMessage>> _getMessage = new();
        private static Dictionary<Type, MessageType> _getType = new();

        private static void Register<T>(MessageType type) where T : NetMessage, new() {
            _getType[typeof(T)] = type;
            _getMessage[type] = () => new T();
        }

        static Net() {
            Register<ScreenReadyMessage>(MessageType.ScreenReady);
            Register<PopScreenMessage>(MessageType.PopScreen);
            Register<TransitionMessage>(MessageType.Transition);

            Register<ConnectedMessage>(MessageType.ConnectedMessage);

            Register<FieldScreenMessage>(MessageType.FieldScreen);
            Register<FieldModelMessage>(MessageType.FieldModel);
            Register<FieldBGMessage>(MessageType.FieldBG);
            Register<FieldEntityModelMessage>(MessageType.FieldEntityModel);
            Register<FieldBGScrollMessage>(MessageType.FieldBGScroll);

            Register<UIScreenMessage>(MessageType.UIScreen);
            Register<UIStateMessage>(MessageType.UIState);

            Register<BattleScreenMessage>(MessageType.BattleScreen);
            Register<AddBattleModelMessage>(MessageType.AddBattleModel);
            Register<SetBattleCameraMessage>(MessageType.SetBattleCamera);
            Register<CharacterReadyMessage>(MessageType.BattleCharacterReady);
            Register<SwirlMessage>(MessageType.SwirlScreen);
            Register<TargetOptionsMessage>(MessageType.TargetOptions);

            Register<CycleBattleMenuMessage>(MessageType.CycleBattleMenu);
            Register<GetTargetOptionsMessage>(MessageType.GetTargetOptions);
            Register<QueueActionMessage>(MessageType.QueueAction);

            Register<SfxMessage>(MessageType.SfxMessage);
            Register<MusicMessage>(MessageType.MusicMessage);
            Register<MusicVolumeMessage>(MessageType.MusicVolumeMessage);
            Register<SfxChannelMessage>(MessageType.SfxChannelMessage);
        }

        protected NetMessage GetMessage(MessageType type) => _getMessage[type]();
        protected MessageType GetMessageType(NetMessage message) => _getType[message.GetType()];

        public abstract string Status { get; }

        public abstract void Send(NetMessage message);
        public abstract void SendTo(NetMessage message, Guid playerID);
        public abstract void Update();
        public abstract void Shutdown();



        private Dictionary<Type, List<(object obj, Action<ServerMessage> dispatch)>> _listeners = new();
        private Dictionary<Type, List<(object obj, Action<ClientMessage, Guid> dispatch)>> _clientListeners = new();

        public void Listen<T>(IListen<T> listener) where T : ServerMessage {
            if (!_listeners.TryGetValue(typeof(T), out var list)) {
                _listeners[typeof(T)] = list = new List<(object obj, Action<ServerMessage> dispatch)>();
            }
            Action<ServerMessage> dispatch = msg => listener.Received(msg as T);
            list.Add((listener, dispatch));
        }
        public void Listen<T>(IClientListen<T> listener) where T : ClientMessage {
            if (!_clientListeners.TryGetValue(typeof(T), out var list)) {
                _clientListeners[typeof(T)] = list = new List<(object obj, Action<ClientMessage, Guid> dispatch)>();
            }
            Action<ClientMessage, Guid> dispatch = (msg, id) => listener.Received(msg as T, id);
            list.Add((listener, dispatch));
        }

        public void Unlisten(object listener) {
            foreach (var list in _listeners.Values)
                list.RemoveAll(a => a.obj == listener);
            foreach (var list in _clientListeners.Values)
                list.RemoveAll(a => a.obj == listener);
        }

        protected void Dispatch(ServerMessage message) {
            System.Diagnostics.Trace.WriteLine($"Dispatching message {message.GetType()}");
            if (_listeners.TryGetValue(message.GetType(), out var list)) {
                foreach(var listener in list.ToArray())
                    listener.dispatch(message);
            }
        }
        protected void Dispatch(ClientMessage message, Guid playerID) {
            System.Diagnostics.Trace.WriteLine($"Dispatching message {message.GetType()}");
            if (_clientListeners.TryGetValue(message.GetType(), out var list)) {
                foreach (var listener in list.ToArray())
                    listener.dispatch(message, playerID);
            }
        }
    }

    public static class NetUtil {
        public static void Put(this LiteNetLib.Utils.NetDataWriter writer, Vector3 vector3) {
            writer.Put(vector3.X);
            writer.Put(vector3.Y);
            writer.Put(vector3.Z);
        }

        public static Vector3 GetVec3(this LiteNetLib.Utils.NetDataReader reader) {
            return new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        }

        public static void Put(this LiteNetLib.Utils.NetDataWriter writer, Guid guid) {
            writer.PutBytesWithLength(guid.ToByteArray());
        }

        public static Guid GetGuid(this LiteNetLib.Utils.NetDataReader reader) {
            return new Guid(reader.GetBytesWithLength());
        }
    }

    public abstract class NetMessage {
        public abstract void Save(LiteNetLib.Utils.NetDataWriter writer);
        public abstract void Load(LiteNetLib.Utils.NetDataReader reader);

        protected int GenerateMask(params bool[] items) {
            int mask = 0;
            foreach (int index in Enumerable.Range(0, items.Length))
                if (items[index])
                    mask |= 1 << index;
            return mask;
        }

        protected IEnumerable<int> SetBits(int mask) {
            int index = 0;
            while(mask != 0) {
                if ((mask & 1) != 0)
                    yield return index;
                mask >>= 1;
                index++;
            }
        }
    }

    public abstract class ServerMessage : NetMessage { }
    public abstract class ClientMessage : NetMessage { }

    public abstract class ChangeScreenMessage : ServerMessage {

        public abstract Screen GetScreen();
    }

    public class ScreenReadyMessage : ServerMessage {
        public override void Load(NetDataReader reader) {
        }

        public override void Save(NetDataWriter writer) {
        }
    }

    public class PopScreenMessage : ServerMessage {
        public override void Load(NetDataReader reader) {
        }

        public override void Save(NetDataWriter writer) {
        }
    }


    public enum TransitionKind {
        FadeIn,
        FadeOut,
    }
    public class TransitionMessage : ServerMessage {
        public TransitionKind Kind { get; set; }

        public override void Load(NetDataReader reader) {
            Kind = (TransitionKind)reader.GetInt();
        }

        public override void Save(NetDataWriter writer) {
            writer.Put((int)Kind);
        }
    }

    public class ConnectedMessage : ServerMessage {
        public Guid PlayerID { get; set; }

        public override void Load(NetDataReader reader) {
            PlayerID = reader.GetGuid();
        }

        public override void Save(NetDataWriter writer) {
            writer.Put(PlayerID);
        }
    }
}