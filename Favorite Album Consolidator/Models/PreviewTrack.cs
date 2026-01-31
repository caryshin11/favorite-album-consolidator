using System;
using System.Collections.Generic;
using System.Text;

namespace Favorite_Album_Consolidator.Models
{
    public sealed class PreviewTrack
    {
        public string PreviewUrl { get; set; } = "";
        public string ArtistName { get; set; } = "";
        public string TrackName { get; set; } = "";

        public override string ToString() => $"{ArtistName} - {TrackName}";
    }
}
