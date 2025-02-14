﻿// This program and the accompanying materials are made available under the terms of the
//  Eclipse Public License v2.0 which accompanies this distribution, and is available at
//  https://www.eclipse.org/legal/epl-v20.html
//  
//  SPDX-License-Identifier: EPL-2.0

using Ficedula.FF7;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Braver.Battle {

    public enum AttackFormula {
        Physical,
        Magical,
        Cure,
        Item,
        HPPercent,
        MaxHPPercent,
        Fixed,
        Recovery,
        Custom,
    }

    public struct Ability {

        public Statuses InflictStatus { get; set; }
        public Statuses RemoveStatus { get; set; }
        public Statuses ToggleStatus { get; set; }
        public int StatusChance { get; set; }

        public Elements Elements { get; set; }

        public int Power { get; set; }
        public AttackFormula Formula { get; set; }

        public int PAtPercent { get; set; }
        public int MAtPercent { get; set; }
        public bool IsPhysical { get; set; }
        public bool IsMagical { get; set; }
        public bool IsReflectable { get; set; }
        public bool AutoCritical { get; set; }
        public bool NoSplit { get; set; }
        public bool LongRange { get; set; }
        public bool IsQuadraMagic { get; set; }
        public bool DamageMP { get; set; }
        public int MPTurboLevel { get; set; }
        public bool IsRestore { get; set; }

        public bool HasElement(Element e) {
            if (e == Element.None)
                return false;
           
            Elements mask = (Elements)(1 << ((int)e - 1));
            return (Elements & mask) != 0;
        }
    }

    public static class AbilityExtensions {

        public static Ability ToAbility(this Ficedula.FF7.Battle.Attack attack, ICombatant source) {
            bool critical;
            bool physical;
            bool autoHit;

            switch (attack.DamageType >> 4) {
                case 0x0:
                case 0x3:
                    physical = true; critical = false; autoHit = true;
                    break;
                case 0x1:
                    physical = true; critical = true; autoHit = false;
                    break;
                case 0x2:
                    physical = false;critical = false;autoHit = false;
                    break;
                case 0x4:
                case 0x5:
                    physical = false; critical = false; autoHit = true;
                    break;
                case 0xb:
                    physical = true; critical = false; autoHit = false; 
                    break;
                default:
                    throw new NotImplementedException();
            }

            AttackFormula formula;

            bool noSplit = true;

            int power = attack.Power;

            switch (attack.DamageType & 0xf) {
                case 0x0:
                    formula = AttackFormula.Fixed;
                    power = 0;
                    break;
                case 0x1:
                    formula = AttackFormula.Physical; noSplit = false;
                    break;
                case 0x2:
                    formula = AttackFormula.Magical;
                    break;
                case 0x3:
                    formula = AttackFormula.HPPercent;
                    break;
                case 0x4:
                    formula = AttackFormula.MaxHPPercent;
                    break;
                case 0x5:
                    formula = AttackFormula.Cure;
                    break;
                case 0x6:
                    formula = AttackFormula.Fixed;
                    break;
                case 0x7:
                    formula = AttackFormula.Item;
                    break;
                case 0x8:
                    formula = AttackFormula.Recovery;
                    break;
                default:
                    throw new NotImplementedException();

            }

            Statuses inflict, cure, toggle;
            switch (attack.StatusType) {
                case Ficedula.FF7.Battle.AttackStatusType.Inflict:
                    inflict = attack.Statuses;
                    cure = toggle = Statuses.None;
                    break;
                case Ficedula.FF7.Battle.AttackStatusType.Cure:
                    cure = attack.Statuses;
                    inflict = toggle = Statuses.None;
                    break;
                case Ficedula.FF7.Battle.AttackStatusType.Toggle:
                    toggle = attack.Statuses;
                    cure = inflict = Statuses.None;
                    break;
                default:
                    inflict = cure = toggle = Statuses.None;
                    break;
            }

            return new Ability {
                Power = power,
                IsReflectable = attack.SpecialAttackFlags.HasFlag(Ficedula.FF7.Battle.SpecialAttackFlags.Reflectable),
                DamageMP = attack.SpecialAttackFlags.HasFlag(Ficedula.FF7.Battle.SpecialAttackFlags.DamageMP),
                Formula = formula,
                IsMagical = !physical,
                IsPhysical = physical,
                //IsRestore //TODO!!!!
                AutoCritical = attack.SpecialAttackFlags.HasFlag(Ficedula.FF7.Battle.SpecialAttackFlags.AlwaysCritical),
                InflictStatus = inflict,
                RemoveStatus = cure,
                ToggleStatus = toggle,
                StatusChance = attack.StatusChance,
                Elements = attack.Elements,
                IsQuadraMagic = false,
                MPTurboLevel = 0,
                MAtPercent = attack.AttackPC,
                PAtPercent = attack.AttackPC,
                NoSplit = noSplit,
            };
        }

    }

    public class Attacks : Cacheable {

        private Ficedula.FF7.Battle.AttackCollection _attacks;

        public Ficedula.FF7.Battle.Attack this[int index] => _attacks.Attacks[index];
        public int Count => _attacks.Attacks.Count;


        public override void Init(BGame g) {
            var kernel = g.Singleton<KernelCache>();
            _attacks = new Ficedula.FF7.Battle.AttackCollection(
                new MemoryStream(kernel.Kernel.Sections[1])
            );
        }
    }
}
