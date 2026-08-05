import { Routes } from '@angular/router';
import { guardaAutenticado, guardaDono } from './nucleo/seguranca/guardas';
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
      { path: 'funil', loadComponent: () => import('./paginas/funil/funil').then(m => m.Funil) },
      { path: 'contatos', loadComponent: () => import('./paginas/contatos/contatos').then(m => m.Contatos) },
      // Detalhe DEPOIS da lista: a rota mais específica não pode ser sombreada pela genérica.
      { path: 'contatos/:id', loadComponent: () => import('./paginas/contato/contato').then(m => m.Contato) },

      // Configuração: só o DONO. O guard é conveniência de UX — o enforcement real é o
      // [Authorize(Roles="dono")] no controller.
      {
        path: 'equipe', canActivate: [guardaDono],
        loadComponent: () => import('./paginas/equipe/equipe').then(m => m.Equipe)
      },
      {
        path: 'conexao', canActivate: [guardaDono],
        loadComponent: () => import('./paginas/conexao/conexao').then(m => m.Conexao)
      },

      {
        path: 'configuracoes', canActivate: [guardaDono],
        loadComponent: () => import('./paginas/configuracoes/configuracoes').then(m => m.Configuracoes)
      },

      // As etapas do funil. Configuração, e por isso separada do /funil — lá é o quadro, a
      // operação diária de qualquer papel; aqui é a FORMA do funil, e só o dono muda.
      {
        path: 'etapas', canActivate: [guardaDono],
        loadComponent: () => import('./paginas/etapas/etapas').then(m => m.Etapas)
      },

      // Formulários do site: tela PRÓPRIA, não uma seção a mais em Configurações. Ela tem
      // credencial na tela, blocos de código para copiar e um botão destrutivo — misturar isso
      // com janela de atendimento e feriado deixaria as duas coisas piores.
      {
        path: 'formularios', canActivate: [guardaDono],
        loadComponent: () => import('./paginas/formularios/formularios').then(m => m.Formularios)
      },

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
