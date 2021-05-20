﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;


namespace Indirect.Converters
{
    class LikeUnlikeStyleConverter : IValueConverter
    {
        public Style LikeStyle { get; set; }
        public Style UnlikeStyle { get; set; }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b)
            {
                return b ? UnlikeStyle : LikeStyle;
            }

            return LikeStyle;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
