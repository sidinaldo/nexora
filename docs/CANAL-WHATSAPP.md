# INT-XX — Canal de WhatsApp: Evolution ou API oficial da Meta, por conexão

Cada número (conexão) escolhe o canal ao ser criado:

| canal | como conecta | texto livre | custo |
|---|---|---|---|
| **Não oficial** (Evolution/Baileys) | QR code; o número continua no celular | a qualquer hora | nenhum na Meta |
| **Oficial** (Meta Cloud API) | credenciais da Meta; o número **sai do aplicativo** | só nas 24h depois da última mensagem do cliente | por conversa, cobrado pela Meta |

O resto do sistema não sabe qual é: caixa, funil, follow-up e NPS funcionam igual. A conexão
**nunca troca de canal** — trocar é criar outra e apagar a antiga. O histórico do aparelho não vem.

A empresa tem um **canal padrão** (tela de Conexão), que só pré-seleciona o formulário.

---

## 1. Ligar um número na API oficial

**Na Meta** (developers.facebook.com, app do tipo Empresa com o produto WhatsApp):

1. O número registrado na conta do WhatsApp Business (WABA). Anote o **Phone Number ID** e o
   **WABA ID** (WhatsApp → Configuração da API).
2. Um **token permanente** de usuário do sistema, com `whatsapp_business_messaging` e
   `whatsapp_business_management`. O token de 24h do painel serve só para teste.
3. O **App secret** (Configurações do app → Básico).

**No Nexora** (Conexão → novo número → API oficial):

4. Preencha os quatro campos e confirme o aviso. O Nexora confere na Meta antes de salvar: o
   token lê o número, e o número é daquela WABA. Os segredos ficam cifrados e nunca voltam à tela.
5. Abra a conexão e copie a **URL de retorno** e o **Verify token**.

**De volta na Meta** (WhatsApp → Configuração → Webhook):

6. Cole a URL e o verify token. A Meta chama o Nexora para confirmar — a conexão mostra
   "Confirmado pela Meta em…".
7. Inscreva os campos **`messages`** e **`message_template_status_update`**.
8. No Nexora, **Testar conexão**: tem de dizer "Tudo certo".

---

## 2. A janela de 24h

Na API oficial, texto, anexo e áudio só saem até 24h depois da **última mensagem do cliente**
(`conversas.ultima_entrada_em`). Depois disso, só **template aprovado**. As 72h de anúncio são só de
preço, não liberam texto.

- A caixa e o card do funil mostram a janela: aberta (tempo restante), fechando (menos de 2h),
  fechada. Na Evolution é só informação.
- Com ela fechada, o compositor dá lugar aos templates aprovados do número, já preenchidos.
- O servidor recusa antes de gravar (409 `janela_fechada`): nada fica "não enviado" à toa.

---

## 3. Templates

Ficam no painel da conexão oficial. O ciclo:

**rascunho** (editável) → **em revisão na Meta** → **aprovado** ou **recusado** (com o motivo)

- Variáveis por nome, de uma lista fechada: `{{nome}}` (primeiro nome do cliente), `{{empresa}}`,
  `{{vendedor}}` (quem envia; sem ninguém, a empresa). O Nexora numera para a Meta.
- A Meta não aceita texto que começa ou termina com variável — o Nexora recusa antes.
- Categoria: **utilidade** (sobre algo que o cliente pediu) ou **marketing** (oferta). Marketing
  custa mais e tem limite por cliente.
- A decisão da Meta chega pelo webhook e, como reforço, por consulta a cada 5 minutos.
- O template é **do número que o criou**: só sai pelas conversas dele.

---

## 4. Automações com a janela fechada

Follow-up, lembrete com mensagem e pesquisa pós-venda saem dias depois — quase sempre com a janela
fechada. Em **Configurações → Templates das automações** o dono escolhe o template de cada uma.

| situação | follow-up / lembrete | pesquisa pós-venda |
|---|---|---|
| Evolution, ou janela aberta | texto de sempre | texto de sempre |
| janela fechada, com template aprovado | sai o template | sai o template |
| janela fechada, sem template | **não sai**; a conversa mostra o motivo e o lembrete conclui | **espera**: tenta no dia seguinte, até expirar |

---

## 5. Deploy

- **`SEGREDOS_CHAVE_CIFRA` é obrigatória** (32 bytes em base64: `openssl rand -base64 32`). Sem ela
  a API não sobe. Ela cifra o token e o app secret das conexões oficiais.
- **Guarde a chave junto com a senha do backup.** Backup restaurado sem ela tem as conexões
  oficiais sem credencial: cada uma precisa do token e do app secret cadastrados de novo.
- O webhook da Meta precisa de HTTPS público: `https://<api>/api/webhook/meta`.

---

## 6. Limites conhecidos

- **Uma conversa por número (CONV-XX).** O mesmo cliente escrevendo para dois números da empresa
  tem duas conversas, cada uma com fila, dono e janela próprios, e a resposta sai pelo número de
  cada uma. Onde o sistema precisa de "a conversa do contato" (tela do contato, card do funil,
  lembrete, follow-up, NPS), usa a **principal**: a de mensagem mais recente
  (`RegrasConversa.Principal`).
  - O histórico de antes do CONV-XX não foi dividido: o que já tinha se misturado continua na
    conversa onde está.
  - Começar conversa por um número que o cliente nunca usou não existe: ela nasce quando ele
    escreve.
  - Integrações que recebem nossos webhooks de saída passam a ver mais de um `conversaId` para o
    mesmo contato.
- **Os nomes de campo da Meta vêm da documentação**, não de entregas reais. No primeiro número em
  produção, conferir mensagem, mídia, status e anúncio (`referral`); tipo desconhecido aparece na
  thread como "o cliente mandou algo que o Nexora ainda não mostra", com o tipo no fim, e é por ele
  que se descobre o que falta.
- **Taxa do webhook**: a rota aceita 300/min por IP, e cada mensagem enviada gera uns três status.
  Acompanhar com volume.
- Template de **autenticação** (código de acesso) não é criado pelo Nexora.

---

## 7. Onde está no código

| parte | arquivo |
|---|---|
| escolher o canal no envio | `Infra/Whatsapp/RoteadorWhatsApp.cs` (atrás de `IClienteWhatsApp`) |
| Graph API | `Infra/CloudApi/ClienteCloudApi.cs` (versão fixa em `Versao`) |
| recebimento comum aos dois canais | `Infra/Whatsapp/RecepcaoMensagem.cs` |
| webhook: assinatura, fila e processamento | `RecepcaoWebhookMeta`, `MotorWebhooksMeta`, `ProcessadorWebhookCloudApi` |
| janela de 24h | `Core/Whatsapp/Janela24h.cs` |
| templates | `Infra/Servicos/ServicoModelos.cs`, `Core/Whatsapp/PreenchedorModelo.cs`, `RevisaoModelo.cs` |
| automações | `Infra/Whatsapp/SaidaDaAutomatica.cs`, consultada pelo `EnviadorMensagem` |
| cifra dos segredos | `Core/Seguranca/CifraSegredos.cs` |
