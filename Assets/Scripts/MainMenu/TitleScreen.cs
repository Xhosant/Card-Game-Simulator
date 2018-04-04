﻿using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class TitleScreen : MonoBehaviour
{
    public Text versionText;

    void Start()
    {
        versionText.text = MainMenu.VersionMessage + Application.version;
    }

    void Update ()
    {
        if (Input.anyKeyDown)
            SceneManager.LoadScene(MainMenu.MainMenuSceneIndex);
    }
}
