﻿// This program and the accompanying materials are made available under the terms of the
//  Eclipse Public License v2.0 which accompanies this distribution, and is available at
//  https://www.eclipse.org/legal/epl-v20.html
//  
//  SPDX-License-Identifier: EPL-2.0

using Braver.Battle.Effects;
using Ficedula.FF7.Battle;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Braver.Battle {
    public class AnimScriptExecutor {

        private ICombatant _source;
        private ICombatant[] _targets;
        private RealBattleScreen _screen;
        private Engine _engine;
        private AnimationScriptDecoder _script;

        private Func<bool> _shouldContinue = null;
        private bool _paused, _complete;

        private List<Action> _renderers = new();

        public bool IsComplete => !_paused && _complete;

        public AnimScriptExecutor(ICombatant source, ICombatant[] targets, RealBattleScreen screen, Engine engine, AnimationScriptDecoder script) {
            _source = source;
            _targets = targets;
            _screen = screen;
            _engine = engine;
            _script = script;
        }

        public void Step() {
            if (_paused) {
                if (_shouldContinue()) {
                    _paused = false;
                    _shouldContinue = null;
                }
            }
            while (!_paused && !_complete) {
                var op = _script.DecodeNext();
                if (op == null)
                    _complete = true;
                else {
                    var model = _screen.Models[_source];
                    if ((byte)op.Value.Op < 0x8E) {
                        model.PlayAnimation((byte)op.Value.Op, false, 1f, onlyIfDifferent: false);
                        _paused = true;
                        _shouldContinue = () => model.AnimationState.CompletionCount > 0;
                    } else {

                        switch(op.Value.Op) {
                            case AnimScriptOp.MagicCharge:
                                var effect = new Charge(_screen.Graphics, _screen.Game.Open("battle", "jo_b02.tex"));
                                //jo_b03 - limit?, jo_b04 - eskill?
                                //TODO cache this!
                                bool done = false;
                                int frame = 0;
                                Action effRender = null;
                                var pos = model.Translation + model.Translation2;
                                effRender = () => {
                                    if (effect.Render(pos, _screen.View3D, frame++))
                                        _renderers.Remove(effRender);
                                };
                                _renderers.Add(effRender);
                                _screen.Game.Audio.PlaySfx(Sfx.CastMagic, 1f, 1f); //TODO 3d positioning would be nice!
                                break;

                            case AnimScriptOp.ResetStandingPosition:
                                model.Translation2 = Vector3.Zero;
                                break;

                            case AnimScriptOp.KeepAnimOffset:
                                model.Translation2 = model.CurrentAnimOffset.WithY(0);
                                break;

                            case AnimScriptOp.WaitForEffectLoad:
                                //TODO!!!
                                _paused = true;
                                int timeout = 120;
                                _shouldContinue = () => --timeout == 0;
                                break;

                            default:
                                System.Diagnostics.Trace.WriteLine($"Skipping unimplemented op {op.Value.Op}");
                                break;
                        }
                    }

                }
            }
        }

        public void Render() {
            foreach (var render in _renderers.ToArray())
                render();
        }
    }
}
