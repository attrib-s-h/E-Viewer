﻿using HtmlAgilityPack;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ExClient
{
    public sealed class FavoriteCollection : IReadOnlyList<FavoriteCategory>
    {
        private static readonly Regex favStyleMatcher = new Regex(@"background-position:\s*0\s*px\s+-(\d+)\s*px", RegexOptions.Compiled);

        internal FavoriteCategory GetCategory(HtmlNode favoriteIconNode)
        {
            if(favoriteIconNode == null)
                return null;
            var favName = HtmlEntity.DeEntitize(favoriteIconNode.GetAttributeValue("title", null));
            if(favName == null)
                return null;
            var favStyle = favoriteIconNode.GetAttributeValue("style", "");
            var mat = favStyleMatcher.Match(favStyle);
            if(!mat.Success)
                return null;
            var favImgOffset = int.Parse(mat.Groups[1].Value);
            var favIdx = favImgOffset / 19;
            var fav = this[favIdx];
            fav.CollectionName = favName;
            return fav;
        }

        internal FavoriteCollection()
        {
            this.data = new FavoriteCategory[10];
            for(int i = 0; i < data.Length; i++)
            {
                data[i] = new FavoriteCategory(i);
            }
        }

        private FavoriteCategory[] data;

        public int Count => data.Length;

        public FavoriteCategory this[int index] => data[index];

        public IEnumerator<FavoriteCategory> GetEnumerator()
        {
            for(int i = 0; i < data.Length; i++)
                yield return data[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}