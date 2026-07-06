using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
public class CustomizationGroupSwitcher : MonoBehaviour
{
    public CustomizableGroup GroupToSwitch;
    public Text GroupName;
    public Button Next, Previous;
    void Start()
    {
        GroupName.text = GroupToSwitch.gameObject.name;
        Next.onClick.AddListener(GroupToSwitch.NextItem);
        Previous.onClick.AddListener(GroupToSwitch.PreviousItem);
    }
}
