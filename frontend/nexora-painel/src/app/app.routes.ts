import { inject } from '@angular/core';
import { Router, Routes } from '@angular/router';
import { guardaAutenticado, guardaPermissao } from './nucleo/seguranca/guardas';
import { Shell } from './layout/shell/shell';

export const routes: Routes = [
  // ---------- fluxos PÚBLICOS (sem sessão), fora do shell ----------
  {
    path: 'entrar',
    loadComponent: () => import('./paginas/login/login').then(m => m.Login)
  },
  // Aceite de convite: o convidado ainda não tem senha.
  {
    path: 'convite/:token',
    loadComponent: () => import('./paginas/convite/convite').then(m => m.Convite)
  },
  // "Esqueci minha senha": pede o e-mail e dispara o link. Público — quem esqueceu a senha não
  // tem sessão. Responde igual exista a conta ou não.
  {
    path: 'esqueci',
    loadComponent: () => import('./paginas/esqueci/esqueci').then(m => m.Esqueci)
  },
  // Redefinição por link: quem perdeu o acesso não tem sessão.
  {
    path: 'redefinir/:token',
    loadComponent: () => import('./paginas/redefinir/redefinir').then(m => m.Redefinir)
  },

  // ---------- o painel ----------
  {
    path: '',
    component: Shell,
    canActivate: [guardaAutenticado],
    children: [
      // Primeiros passos. Qualquer papel LÊ (o vendedor numa conta recém-criada também merece
      // saber que o WhatsApp ainda não foi conectado); só o dono dispensa.
      { path: 'comecar', loadComponent: () => import('./paginas/comecar/comecar').then(m => m.Comecar) },

      { path: 'caixa', loadComponent: () => import('./paginas/caixa/caixa').then(m => m.Caixa) },

      { path: 'dashboard', loadComponent: () => import('./paginas/dashboard/dashboard').then(m => m.Dashboard) },
      { path: 'meu-dia', loadComponent: () => import('./paginas/meu-dia/meu-dia').then(m => m.MeuDia) },
      // ===================== O CRM: UM QUADRO POR PIPELINE =====================
      // `/crm/:pipeline` é a PRIMEIRA rota de navegação principal do projeto com parâmetro — até
      // aqui só `contatos/:id`, que é folha, e os dois tokens públicos tinham um.
      //
      // ⚠️ O componente é REUTILIZADO entre `/crm/1` e `/crm/2`: o Angular não o destrói, só
      // troca o parâmetro. Quem lê `rota.snapshot` uma vez no `ngOnInit` desenha o primeiro funil
      // e nunca mais muda — foi exatamente assim que a caixa de entrada quebrou quando passou a
      // usar `?conversa=`. Por isso o `Funil` assina `paramMap`.
      //
      // `/crm` sem id redireciona para a pipeline padrão, resolvida no servidor. E `/funil`
      // continua existindo, redirecionando: há link antigo em e-mail, no dashboard e na memória
      // de quem usa o produto todo dia.
      { path: 'crm/:pipeline', loadComponent: () => import('./paginas/funil/funil').then(m => m.Funil) },
      { path: 'crm', loadComponent: () => import('./paginas/funil/funil').then(m => m.Funil) },
      { path: 'funil', redirectTo: 'crm', pathMatch: 'full' },
      { path: 'contatos', loadComponent: () => import('./paginas/contatos/contatos').then(m => m.Contatos) },

      // Importar leads de arquivo (INT-XX). Tela, e não modal: o mapeamento é uma tabela com uma
      // linha por coluna do arquivo do cliente. Ela ABSORVEU o modal de dois passos da issue #8 —
      // duas telas de importar seriam a duplicata que este projeto passa o tempo desmontando.
      {
        path: 'importar', canActivate: [guardaPermissao('importar_contatos')],
        loadComponent: () => import('./paginas/importar/importar').then(m => m.Importar)
      },
      // SEM guarda de permissão: vendedor vê relatório, o dele. O recorte é por LINHA e mora na API.
      { path: 'relatorios', loadComponent: () => import('./paginas/relatorios/relatorios').then(m => m.Relatorios) },
      // Detalhe DEPOIS da lista: a rota mais específica não pode ser sombreada pela genérica.
      { path: 'contatos/:id', loadComponent: () => import('./paginas/contato/contato').then(m => m.Contato) },

      // Configuração: só o DONO. O guard é conveniência de UX — o enforcement real é o
      // [Authorize(Roles="dono")] no controller.
      {
        path: 'equipe', canActivate: [guardaPermissao('gerenciar_equipe')],
        loadComponent: () => import('./paginas/equipe/equipe').then(m => m.Equipe)
      },
      {
        path: 'conexao', canActivate: [guardaPermissao('configurar_empresa')],
        loadComponent: () => import('./paginas/conexao/conexao').then(m => m.Conexao)
      },

      {
        path: 'configuracoes', canActivate: [guardaPermissao('configurar_empresa')],
        loadComponent: () => import('./paginas/configuracoes/configuracoes').then(m => m.Configuracoes)
      },

      // As etapas de UMA pipeline. Saiu do grupo Configuração do menu: com etapas por funil, uma
      // tela única teria que perguntar "de qual?" antes de mostrar qualquer coisa. Chega-se a ela
      // pelo quadro e pela tela de funis.
      {
        path: 'crm/:pipeline/etapas', canActivate: [guardaPermissao('configurar_empresa')],
        loadComponent: () => import('./paginas/etapas/etapas').then(m => m.Etapas)
      },
      { path: 'etapas', redirectTo: 'pipelines', pathMatch: 'full' },

      // A gestão dos funis: criar, renomear, escolher o padrão, apagar.
      {
        path: 'pipelines', canActivate: [guardaPermissao('configurar_empresa')],
        loadComponent: () => import('./paginas/pipelines/pipelines').then(m => m.Pipelines)
      },

      // O vocabulario de etiquetas. Configuracao, como as etapas: define como a empresa
      // inteira nomeia as coisas. APLICAR a etiqueta sera de qualquer papel — mas isso vive nas
      // telas de operacao, nao aqui.
      {
        path: 'etiquetas', canActivate: [guardaPermissao('configurar_empresa')],
        loadComponent: () => import('./paginas/etiquetas/etiquetas').then(m => m.Etiquetas)
      },

      // ===================== CAPTAÇÃO (NAV-1) =====================
      // Formulário do site e QR/link viraram ABAS de uma tela só: respondem à mesma pergunta do
      // cliente ("de onde meus leads vêm"), compartilham a estatística e o modo de uso.
      //
      // Tela PRÓPRIA, não uma seção a mais em Configurações: lá é formulário de AJUSTE (dados,
      // janela, semáforo, feriados); aqui é superfície de GESTÃO, com lista, número por item,
      // código para copiar e arquivo para baixar.
      {
        path: 'captacao', canActivate: [guardaPermissao('configurar_empresa')],
        loadComponent: () => import('./paginas/captacao/captacao').then(m => m.Captacao)
      },

      // As rotas antigas continuam valendo: alguém já pode ter salvado o link, e havia menu para
      // as duas. Cada uma cai na ABA certa — mandar as duas para a primeira faria quem salvou o
      // link do QR chegar em formulários sem entender por quê.
      {
        path: 'formularios',
        redirectTo: () => inject(Router).parseUrl('/captacao')
      },
      {
        path: 'canais',
        redirectTo: () => inject(Router).parseUrl('/captacao?aba=qr')
      },

      // Webhook de saída (INT-3). O item de menu correspondente só entrou agora — o NAV-1 deixou
      // registrado que "Integrações" não podia existir antes de haver o que integrar.
      {
        path: 'integracoes', canActivate: [guardaPermissao('configurar_empresa')],
        loadComponent: () => import('./paginas/integracoes/integracoes').then(m => m.Integracoes)
      },

      // O resto do menu, no CELULAR: a barra inferior tem cinco lugares e o painel tem treze
      // destinos (MOB-2). SEM guarda de permissão — a tela mostra a lista que o papel permite, e o
      // enforcement de cada destino continua no guard da rota dele.
      { path: 'mais', loadComponent: () => import('./paginas/mais/mais').then(m => m.Mais) },

      // Self-service: qualquer papel edita a PRÓPRIA conta (nome, e-mail, senha). Nenhuma rota
      // de conta recebe id — o alvo é sempre o usuário do token.
      { path: 'conta', loadComponent: () => import('./paginas/conta/conta').then(m => m.Conta) },
      // A rota antiga da senha continua valendo, redirecionando: havia link para ela na sidebar
      // e em e-mails de convite, e um 404 depois de reorganizar a tela é gratuito.
      { path: 'conta/senha', redirectTo: 'conta', pathMatch: 'full' },

      { path: '', pathMatch: 'full', redirectTo: 'caixa' }
    ]
  },

  { path: '**', redirectTo: '' }
];
