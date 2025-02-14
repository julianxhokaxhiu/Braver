﻿// This program and the accompanying materials are made available under the terms of the
//  Eclipse Public License v2.0 which accompanies this distribution, and is available at
//  https://www.eclipse.org/legal/epl-v20.html
//  
//  SPDX-License-Identifier: EPL-2.0

using Braver.Plugins;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Braver {

    public abstract class Transition {
        private int _frames, _total;
        
        public Action OnComplete { get; private set; }
        public virtual bool RetainAtEnd { get => false; }

        protected Transition(int frames) {
            _total = frames;
        }

        protected abstract void DoRender(SpriteBatch fxBatch, float progress);

        public void Render(SpriteBatch fxBatch) {
            DoRender(fxBatch, 1f * _frames / _total);
        }

        public virtual bool Step() {
            _frames++;
            return _frames > _total;
        }
    }

    public class FadeTransition : Transition {
        private Color _color;
        private byte _from, _to;
        private UI.CompositeImages _images;
        private bool _retain;

        public override bool RetainAtEnd => _retain;

        public FadeTransition(int frames, Color color, byte from, byte to, UI.CompositeImages images, bool retain) : base(frames) {
            _color = color;
            _from = from;
            _to = to;
            _images = images;
        }

        protected override void DoRender(SpriteBatch fxBatch, float progress) {
            fxBatch.Begin(sortMode: SpriteSortMode.Immediate, blendState: BlendState.AlphaBlend);

            _images.Find("white", out var tex, out var source, out bool flip);
            byte alpha = progress >= 1f ? _to : (byte)(_from + (_to - _from) * progress);
            fxBatch.Draw(tex, new Rectangle(0, 0, 9000, 9000), source, _color.WithAlpha(alpha));

            fxBatch.End();
        }
    }

    public abstract class Screen : Net.IListen<Net.ScreenReadyMessage>, IScreen {

        public FGame Game { get; private set; }
        public GraphicsDevice Graphics { get; private set; }
        public bool InputEnabled { get; protected set; } = true;

        public virtual bool ShouldClear => true;
        public abstract Color ClearColor { get; }

        public abstract string Description { get; }

        bool IScreen.HasFinishedLoading => _frames > 0;

        protected SpriteBatch _fxBatch;
        
        private Transition _transition;
        private Action _transitionAction;
        private int _frames = 0;

        protected bool _readyToRender;
        private List<IDisposable> _pluginInstances = new();

        protected PluginInstances<T> GetPlugins<T>(string context) where T : IPluginInstance {
            var instances = Game.PluginManager.GetInstances<T>(context);
            _pluginInstances.Add(instances);
            return instances;
        }

        public virtual void Init(FGame g, GraphicsDevice graphics) {
            Game = g;
            Graphics = graphics;
            _fxBatch = new SpriteBatch(graphics);
            g.Net.Listen<Net.ScreenReadyMessage>(this);

            if (g.Net is Net.Server)
                _readyToRender = true;
        }

        protected abstract void DoStep(GameTime elapsed);
        protected abstract void DoRender();

        public virtual void Reactivated() { }
        public virtual void Suspended() { }
        public virtual void Dispose() {
            foreach (var plugins in _pluginInstances)
                plugins.Dispose();
        }

        public void FadeOut(Action then, int frames = 30) {
            _transition = new FadeTransition(
                frames, Color.Black, 0, 255,
                Game.Singleton(() => new UI.CompositeImages(Graphics, Game)),
                then == null
            );
            _transitionAction = then;
            Game.Net.Send(new Net.TransitionMessage { Kind = Net.TransitionKind.FadeOut });
        }
        public void FadeIn(Action then, int frames = 30) {
            _transition = new FadeTransition(
                frames, Color.Black, 255, 0,
                Game.Singleton(() => new UI.CompositeImages(Graphics, Game)),
                false
            );
            _transitionAction = then;
            Game.Net.Send(new Net.TransitionMessage { Kind = Net.TransitionKind.FadeIn });
        }

        public void Step(GameTime elapsed) {
            DoStep(elapsed);
            if (_transition != null) {
                if (_transition.Step()) {
                    if (!_transition.RetainAtEnd)
                        _transition = null;
                    _transitionAction?.Invoke();
                }
            }
            _frames++;
        }

        public void Render() {
            if (_readyToRender) {
                DoRender();
                if (_transition != null)
                    _transition.Render(_fxBatch);
            }
        }

        public virtual void ProcessInput(InputState input) { }

        public void Received(Net.ScreenReadyMessage message) {
            _readyToRender = true;
        }
    }
}
