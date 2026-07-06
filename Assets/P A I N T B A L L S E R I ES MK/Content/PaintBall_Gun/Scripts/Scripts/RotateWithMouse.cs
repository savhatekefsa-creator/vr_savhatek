using UnityEngine;

public class RotateWithMouse : MonoBehaviour
{
    public float rotationSpeed = 10f;
    public float returnSpeed = 2f;
    private Vector3 mouseOrigin;
    private bool isRotating;

    void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            // Registra a posição do mouse quando o botão esquerdo é pressionado
            mouseOrigin = Input.mousePosition;
            isRotating = true;
        }

        if (Input.GetMouseButtonUp(0))
        {
            // Para de rotacionar quando o botão esquerdo é solto
            isRotating = false;
        }

        if (isRotating)
        {
            // Rotaciona o objeto em torno dos eixos Y e X com base na diferença de posição do mouse
            Vector3 pos = Camera.main.ScreenToViewportPoint(Input.mousePosition - mouseOrigin);
            transform.Rotate(new Vector3(0, -pos.x * rotationSpeed, 0), Space.World);
            transform.Rotate(new Vector3(pos.y * rotationSpeed, 0, 0), Space.Self);
            mouseOrigin = Input.mousePosition;
        }
        else
        {
            // Retorna lentamente à posição de origem
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.identity, Time.deltaTime * returnSpeed);
        }
    }
}
