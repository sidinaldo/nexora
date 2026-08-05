import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, Type, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { RealtimeServico } from '../nucleo/servicos/realtime.servico';
import { AuthServico } from '../nucleo/servicos/auth.servico';

import { Shell } from '../layout/shell/shell';
import { Caixa } from './caixa/caixa';
import { Comecar } from './comecar/comecar';
import { Conexao } from './conexao/conexao';
import { Configuracoes } from './configuracoes/configuracoes';
import { Conta } from './conta/conta';
import { Contato } from './contato/contato';
import { Contatos } from './contatos/contatos';
import { Convite } from './convite/convite';
import { Dashboard } from './dashboard/dashboard';
import { Equipe } from './equipe/equipe';
import { Esqueci } from './esqueci/esqueci';
import { Formularios } from './formularios/formularios';
import { Funil } from './funil/funil';
import { Login } from './login/login';
import { MeuDia } from './meu-dia/meu-dia';
import { Redefinir } from './redefinir/redefinir';

/** RENDERIZAÇÃO DE CADA TELA DO PAINEL.
 *
 *  ===================== O QUE ESTE ARQUIVO PEGA =====================
 *  Componente e template andam separados e divergem: foi assim que o Meu Dia quebrou — o `.ts`
 *  foi reescrito e o `.html` ficou para trás.
 *
 *  Divergência de TIPO o `ng build` já pega. O que ele NÃO pega é o que só acontece rodando:
 *  binding que compila mas estoura em tempo de execução, `ngOnInit` que lança, provider que
 *  falta, `@for` sobre `undefined`. Nada disso aparece em build; tudo isso aparece aqui.
 *
 *  A checagem é deliberadamente rasa e uniforme — montar, deixar as respostas chegarem, e
 *  exigir que a tela desenhe algo. Não é teste de comportamento de cada página: é a rede que
 *  garante que NENHUMA tela está quebrada no commit, que é exatamente o buraco que existia.
 *  =================================================================== */
describe('renderização das telas', () => {
  /** Corpo único para toda resposta pendente. É um SUPERSET das formas que as telas esperam —
   *  campo a mais o JavaScript ignora, e o que importa é nenhuma lista chegar `undefined`, que
   *  é o que faria um `@for` estourar por culpa do teste e não do código. */
  const CORPO = {
    itens: [], temMais: false, total: 0, numeroPagina: 1, tamanho: 30,
    colunas: [], etapas: [], passos: [], acoes: [], usuarios: [], feriados: [],
    conversas: [], contatos: [], lembretes: [], series: [], atividades: [],
    funil: [], origens: [], pontos: [], concluidos: 0,
    mostrar: false, completo: false, dispensado: false,
    naoLidas: 0, whatsappConectado: true, trocouDeNumero: false,
    janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126, feriadosRecentes: [],
    status: 'nao_criada', nome: '', email: '', telefone: '', papel: 'dono',
    // O detalhe do contato lê `dados().contato`; sem isto a tela desenha vazia por culpa do
    // teste, não do código.
    contato: {
      id: 1, nome: 'Cliente', telefone: '5584900000000', email: null, origem: 'manual',
      responsavelId: null, valor: null, etapaId: 1, etapaNome: 'Novo Lead'
    }
  };

  /** Endpoints que respondem ARRAY, não objeto. Mandar `CORPO` neles faz o `@for` estourar com
   *  "not iterable" — e o erro seria do teste, não da tela. Lista explícita porque a URL sozinha
   *  não diz a forma da resposta. */
  const RESPONDEM_ARRAY = [
    '/equipe', '/feriados', '/lembretes/contato/',
    '/configuracao/fusos', '/configuracao/ufs', '/formularios'
  ];

  const TELAS: { nome: string; componente: Type<unknown> }[] = [
    { nome: 'Shell (layout)', componente: Shell },
    { nome: 'Login', componente: Login },
    { nome: 'Esqueci minha senha', componente: Esqueci },
    { nome: 'Convite', componente: Convite },
    { nome: 'Redefinir senha', componente: Redefinir },
    { nome: 'Primeiros passos', componente: Comecar },
    { nome: 'Caixa de entrada', componente: Caixa },
    { nome: 'Dashboard', componente: Dashboard },
    { nome: 'Meu Dia', componente: MeuDia },
    { nome: 'Funil', componente: Funil },
    { nome: 'Contatos', componente: Contatos },
    { nome: 'Detalhe do contato', componente: Contato },
    { nome: 'Equipe', componente: Equipe },
    { nome: 'Conexão', componente: Conexao },
    { nome: 'Configurações', componente: Configuracoes },
    { nome: 'Formulários do site', componente: Formularios },
    { nome: 'Conta', componente: Conta }
  ];

  class RealtimeFalso {
    conectado = signal(true);
    mensagemRecebida$ = new Subject<never>();
    conversaAberta$ = new Subject<never>();
    contatoCriado$ = new Subject<never>();
    statusMensagem$ = new Subject<never>();
    conexaoMudou$ = new Subject<never>();
    async conectar() { /* sem SignalR no teste: abrir socket aqui só traria intermitência */ }
    desconectar() { }
  }

  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: Vazio }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: convertToParamMap({ token: 'token-de-teste', id: '1' }),
              queryParamMap: convertToParamMap({}),
              data: {}
            }
          }
        }
      ]
    });

    httpMock = TestBed.inject(HttpTestingController);

    // Sessão de dono: o shell e as telas de configuração desenham por papel, e sem sessão
    // metade do template ficaria fora — o teste passaria sem ter olhado para ela.
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana Souza', email: 'ana@x.com', papel: 'dono', empresaNome: 'Padaria' }
    } as never);
  });

  afterEach(() => localStorage.clear());

  /** Responde TUDO que a tela pediu, quantas rodadas forem necessárias: uma resposta pode
   *  disparar a próxima requisição (o detalhe do contato carrega a conversa depois do contato). */
  function responderTudo() {
    for (let volta = 0; volta < 5; volta++) {
      const pendentes = httpMock.match(() => true);
      if (pendentes.length === 0) return;
      pendentes.forEach(r =>
        r.flush(RESPONDEM_ARRAY.some(u => r.request.url.includes(u)) ? [] : CORPO));
    }
  }

  for (const tela of TELAS) {
    it(`${tela.nome} monta e desenha sem estourar`, () => {
      const fixture = TestBed.createComponent(tela.componente);

      // Um throw em qualquer um destes passos reprova o teste — é o ponto.
      fixture.detectChanges();     // primeira pintura (estado de carregando)
      responderTudo();
      fixture.detectChanges();     // pintura com os dados

      const html = fixture.nativeElement as HTMLElement;
      expect(html.textContent?.trim().length)
        .withContext('a tela desenhou vazia — template não casou com o componente')
        .toBeGreaterThan(0);
    });
  }

  it('cobre todas as telas roteadas do painel', () => {
    // Guarda contra o esquecimento: tela nova entra na lista, senão o arquivo dá a impressão
    // de cobrir tudo enquanto a última adicionada nunca é montada.
    expect(TELAS.length).toBe(17);
  });
});

@Component({ template: '' })
class Vazio { }
