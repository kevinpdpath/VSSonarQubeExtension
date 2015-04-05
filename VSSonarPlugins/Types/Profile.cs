// --------------------------------------------------------------------------------------------------------------------
// <copyright file="Profile.cs" company="Copyright © 2014 Tekla Corporation. Tekla is a Trimble Company">
//     Copyright (C) 2013 [Jorge Costa, Jorge.Costa@tekla.com]
// </copyright>
// --------------------------------------------------------------------------------------------------------------------
// This program is free software; you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License
// as published by the Free Software Foundation; either version 3 of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty
// of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General Public License for more details. 
// You should have received a copy of the GNU Lesser General Public License along with this program; if not, write to the Free
// Software Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
// --------------------------------------------------------------------------------------------------------------------
namespace VSSonarPlugins.Types
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;

    using PropertyChanged;

    /// <summary>
    /// The profile.
    /// </summary>
    [Serializable]
    [ImplementPropertyChanged]
    public class Profile
    {
        public Profile()
        {
            this.Rules = new Dictionary<string, Rule>();
        }

        /// <summary>
        /// Gets or sets a value indicating whether default.
        /// </summary>
        public bool Default { get; set; }

        /// <summary>
        /// Gets or sets the language.
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// Gets or sets the name.
        /// </summary>
        public string Name { get; set; }

        public string Key { get; set; }

        /// <summary>
        /// Gets or sets the alerts.
        /// </summary>
        public List<Alert> Alerts { get; set; }

        /// <summary>
        /// Gets or sets the rules.
        /// </summary>
        public Dictionary<string, Rule> Rules { get; set; }

        /// <summary>
        /// Gets or sets the projects.
        /// </summary>
        public List<SonarProject> Projects { get; set; }

        /// <summary>
        /// The is rule enabled with repo.
        /// </summary>
        /// <param name="profile">
        /// The profile.
        /// </param>
        /// <param name="idWithRepository">
        /// The id with repository.
        /// </param>
        /// <returns>
        /// The <see cref="Rule"/>.
        /// </returns>
        public static Rule IsRuleEnabled(Profile profile, string internalKey)
        {
            if (profile == null)
            {
                return null;
            }

            try
            {
                return profile.Rules[internalKey];
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        }

        /// <summary>
        /// The is rule present.
        /// </summary>
        /// <param name="key">
        /// The key.
        /// </param>
        /// <returns>
        /// The <see cref="bool"/>.
        /// </returns>
        public bool IsRulePresent(string key)
        {
            return this.Rules.ContainsKey(key);
        }

        /// <summary>The get rule.</summary>
        /// <param name="internalKey">The internal Key.</param>
        /// <returns>The <see cref="Rule"/>.</returns>
        public Rule GetRule(string internalKey)
        {
            try
            {
                return this.Rules[internalKey];
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        }

        /// <summary>
        /// The create rule.
        /// </summary>
        /// <param name="rule">
        /// The rule.
        /// </param>
        public void CreateRule(Rule rule)
        {
            if (!this.IsRulePresent(rule.Key))
            {
                this.Rules.Add(rule.InternalKey, rule);
            }
        }
    }
}