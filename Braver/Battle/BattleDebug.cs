﻿// This program and the accompanying materials are made available under the terms of the
//  Eclipse Public License v2.0 which accompanies this distribution, and is available at
//  https://www.eclipse.org/legal/epl-v20.html
//  
//  SPDX-License-Identifier: EPL-2.0

using Braver.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Text;
using System.Threading.Tasks;

namespace Braver.Battle {
    internal class BattleDebug {
        private UI.UIBatch _ui;
        private Engine _engine;
        private int _cMenu, _anim, _script;
        private RealBattleScreen _screen;
        private AnimScriptExecutor _exec;

        public BattleDebug(GraphicsDevice graphics, FGame g, Engine engine, RealBattleScreen screen) {
            _ui = new UI.UIBatch(graphics, g);
            _engine = engine;
            _screen = screen;
        }

        public void Step() {
            _ui.Reset();

            _ui.DrawText("main", $"Anim: {_anim}", 1100, 50, 0.9f, Color.White);
            _ui.DrawText("main", $"Script: {_script}", 1100, 80, 0.9f, Color.White);

            int y = 150;
            foreach(var chr in _engine.ActiveCombatants) {
                _ui.DrawText("main", chr.Name, 1100, y, 0.9f, Color.White);
                y += 30;
            }
            _ui.DrawImage("pointer", 1100, 150 + 30 * _cMenu, 0.95f, Alignment.Right);

            if (_exec != null) {
                _exec.Step();
                if (_exec.IsComplete)
                    _exec = null;
            }
        }

        public void ProcessInput(InputState input) {
            if (input.IsRepeating(InputKey.Down))
                _cMenu = (_cMenu + 1) % _engine.ActiveCombatants.Count();
            if (input.IsRepeating(InputKey.Up))
                _cMenu = (_cMenu + _engine.ActiveCombatants.Count() - 1) % _engine.ActiveCombatants.Count();

            if (input.IsRepeating(InputKey.Left))
                _anim--;
            if (input.IsRepeating(InputKey.Right))
                _anim++;
            if (input.IsRepeating(InputKey.PanLeft))
                _script--;
            if (input.IsRepeating(InputKey.PanRight))
                _script++;

            if (input.IsJustDown(InputKey.Cancel)) {
                _exec = new AnimScriptExecutor(
                    _engine.ActiveCombatants.ElementAt(_cMenu),
                    _engine.ActiveCombatants.OfType<EnemyCombatant>().ToArray(),
                    _screen, _engine,
                    new Ficedula.FF7.Battle.AnimationScriptDecoder(new byte[] { (byte)_anim, 0 })
                );
            }
            if (input.IsJustDown(InputKey.OK)) {
                var source = _engine.ActiveCombatants.ElementAt(_cMenu);
                var model = _screen.Models[source];
                _exec = new AnimScriptExecutor(
                    source,
                    _engine.ActiveCombatants.OfType<EnemyCombatant>().ToArray(),
                    _screen, _engine,
                    new Ficedula.FF7.Battle.AnimationScriptDecoder(model.AnimationScript.Scripts[_script])
                );
            }

        }

        public void Render() {
            _ui.Render();
            _exec?.Render();
        }
    }
}
