using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CustomizableGroup : MonoBehaviour
{
    public bool OppitionalItem = false;
    public int ItemID = 0;
    public List<GameObject> Childs = new List<GameObject>();
    void Start()
    {
        if (Childs.Count > 0) return;

        //Get all childs
        for (int i = 0; i < transform.childCount; i++)
        {
            Childs.Add(transform.GetChild(i).gameObject);
        }

        if (OppitionalItem)
        {
            //(ItemID == Childs.Count) == null stage
            ItemID = Childs.Count;
        }
        UpdateVisibility(Childs, ItemID);
    }
    public void Update()
    {
        if (Input.GetKeyDown(KeyCode.E)) NextItem();
        if (Input.GetKeyDown(KeyCode.Q)) PreviousItem();
    }
    public void NextItem()
    {
        ItemID++;
        ItemID = (ItemID > Childs.Count) ? 0 : ItemID;

        //(ItemID == Childs.Count) == null stage
        if(OppitionalItem == false && ItemID == Childs.Count)
        {
            ItemID = 0;
        }

        UpdateVisibility(Childs, ItemID);
    }
    public void PreviousItem()
    {
        ItemID--;
        ItemID = (ItemID < 0) ? Childs.Count : ItemID;

        //(ItemID == Childs.Count) == null stage
        if (OppitionalItem == false && ItemID == Childs.Count)
        {
            ItemID = Childs.Count - 1;
        }

        UpdateVisibility(Childs, ItemID);
    }
    public void UpdateVisibility()
    { 
        for(int i = 0; i < Childs.Count; i++)
        {
            if (Childs[i] == null)
            {
                Debug.LogWarning("index [" + i + "] of child list is not assigned"); 
                return;
            }

            bool enable = (i == ItemID);
            Childs[i].SetActive(enable);
        }
    }
    public static void UpdateVisibility(List<GameObject> list, int id)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == null)
            {
                Debug.LogWarning("index [" + i + "] of child list is not assigned");
                return;
            }

            bool enable = (i == id);
            list[i].SetActive(enable);
        }
    }
}
