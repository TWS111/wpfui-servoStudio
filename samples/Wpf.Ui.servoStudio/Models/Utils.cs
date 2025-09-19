// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Windows.Foundation.Collections;

namespace Wpf.Ui.servoStudio.Models;

public class TrulyObservableCollection<T> : ObservableCollection<T>
        where T : INotifyPropertyChanged
{
    public TrulyObservableCollection()
    {
        CollectionChanged += TrulyObservableCollection_CollectionChanged;
    }

    void TrulyObservableCollection_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (Object item in e.NewItems)
            {
                (item as INotifyPropertyChanged).PropertyChanged += item_PropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (Object item in e.OldItems)
            {
                (item as INotifyPropertyChanged).PropertyChanged -= item_PropertyChanged;
            }
        }
    }

    void item_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        var item = (T)sender;
        var index = IndexOf(item);

        if (index >= 0)
        {
            // 用同一实例作为新旧项，通知 UI 该索引位置的项“被替换”（实为属性变化）
            OnCollectionChanged(
                new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Replace, item, item, index));
        }
        else
        {
            // 不在集合中时，退回整集合刷新
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}

