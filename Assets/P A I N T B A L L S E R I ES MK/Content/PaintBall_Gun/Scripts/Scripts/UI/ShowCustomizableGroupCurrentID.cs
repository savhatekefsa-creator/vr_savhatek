using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
[RequireComponent(typeof(Text))]
public class ShowCustomizableGroupCurrentID : MonoBehaviour
{
    private Text text;

    public CustomizableGroup GroupToShowCurrentID;
    void Start()
    {
        text = GetComponent<Text>();
        if(GroupToShowCurrentID == null)
        {
            GroupToShowCurrentID = GetComponentInParent<CustomizationGroupSwitcher>().GroupToSwitch;
        }
    }
    void Update()
    {
        text.text = GroupToShowCurrentID.ItemID.ToString();
    }
}
