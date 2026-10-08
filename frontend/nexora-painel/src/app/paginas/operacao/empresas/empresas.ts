import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChaveOperador } from '../../../nucleo/seguranca/chave-operador';
import { OperadorServico } from '../../../nucleo/servicos/operador.servico';
import { EmpresaNaLista, PlanoDto } from '../../../nucleo/modelos';

/** A lista de clientes, com os números de cada um (OPE-1).
 *
 *  ===================== O QUE ESTA TELA NÃO MOSTRA =====================
 *  Nenhum nome de contato, telefone, texto de mensagem ou e-mail da equipe. Ela responde "como vai
 *  este cliente", não "o que ele está conversando" — e essa linha é de desenho, não de tela: os
 *  campos nem chegam do servidor.
 *  ======================================================================
 *
 *  ⚠️ O EXCEDENTE É UM SEGUNDO PASSO, NÃO UM ERRO. Baixar um teto abaixo do uso é legal — é assim
 *  que se registra um downgrade —, mas é quase sempre engano de digitação. O servidor recusa a
 *  primeira tentativa explicando que ninguém perde acesso, e a tela oferece confirmar. */
@Component({
  selector: 'app-operacao-empresas',
  imports: [FormsModule, DatePipe, DecimalPipe],
  templateUrl: './empresas.html',
  styleUrl: './empresas.css'
})
export class OperacaoEmpresas implements OnInit {
  private servico = inject(OperadorServico);
  private chave = inject(ChaveOperador);

  busca = signal('');
  dias = signal(30);
  pagina = signal(1);
  readonly tamanho = 25;

  carregando = signal(false);
  erro = signal('');
  itens = signal<EmpresaNaLista[]>([]);
  total = signal(0);
  planos = signal<PlanoDto[]>([]);

  /** Qual linha está aberta para edição. Null = nenhuma. */
  editando = signal<number | null>(null);
  planoEscolhido = signal<number | null>(null);
  limiteConexoes = signal(1);
  limiteUsuarios = signal(3);
  salvando = signal(false);
  erroEdicao = signal('');
  /** O servidor pediu confirmação do excedente — o TEXTO a explicar. */
  excedente = signal('');

  /** ===================== QUAL AÇÃO PEDIU A CONFIRMAÇÃO =====================
   *
   *  ⚠️ ESTE SINAL FALTAVA, E O DEFEITO ERA MUDO. O botão "Aplicar mesmo assim" serve DUAS ações —
   *  atribuir plano e salvar limites — e chamava sempre `salvarLimites(true)`. Quem escolhia um
   *  plano, levava o 409 e confirmava acabava salvando os LIMITES: o plano nunca era atribuído,
   *  `AjustarLimitesAsync` não mexe em `plano_id` de propósito, a tela recarregava com sucesso e a
   *  coluna "Plano" continuava com um travessão. Nenhum erro, nenhuma pista.
   *
   *  O comentário do `excedente` já dizia "guarda o que explicar E O QUE REENVIAR" — a segunda
   *  metade nunca existiu.
   *  ========================================================================= */
  acaoDoExcedente = signal<'plano' | 'limites' | null>(null);

  ngOnInit() {
    this.carregar();
    this.servico.planos().subscribe({
      next: p => this.planos.set(p.filter(x => x.ativo)),
      error: () => { }      // o catálogo é conveniência aqui; a lista funciona sem ele
    });
  }

  carregar() {
    this.carregando.set(true);
    this.erro.set('');
    this.servico.empresas(this.busca().trim(), this.pagina(), this.tamanho, this.dias()).subscribe({
      next: p => {
        this.itens.set(p.itens);
        this.total.set(p.totalCount);
        this.totalPaginas.set(p.totalPaginas);
        this.carregando.set(false);
      },
      error: e => {
        this.carregando.set(false);
        // 401 na LISTA: não há nada preenchido para perder, então volta a pedir a chave — que é o
        // que o operador faria de qualquer jeito.
        if (e.status === 401) { this.chave.limpar(); return; }
        this.erro.set(e.error?.erro ?? 'Não foi possível carregar.');
      }
    });
  }

  buscar() { this.pagina.set(1); this.carregar(); }

  /** Quantas páginas há, do servidor (AUD-XX, #21). A tela dividia o total pelo tamanho. */
  totalPaginas = signal(1);
  paginas(): number { return this.totalPaginas(); }

  irPara(n: number) {
    if (n < 1 || n > this.paginas()) return;
    this.pagina.set(n);
    this.carregar();
  }

  abrir(e: EmpresaNaLista) {
    this.editando.set(e.id);
    this.planoEscolhido.set(e.planoId);
    this.limiteConexoes.set(e.limiteConexoes);
    this.limiteUsuarios.set(e.limiteUsuarios);
    this.erroEdicao.set('');
    this.excedente.set('');
  }

  fechar() { this.editando.set(null); }

  salvarLimites(confirmar = false) {
    const id = this.editando();
    if (id === null) return;

    this.salvando.set(true);
    this.erroEdicao.set('');
    this.acaoDoExcedente.set('limites');
    if (confirmar) this.excedente.set('');

    this.servico.ajustarLimites(id, {
      limiteConexoes: this.limiteConexoes(),
      limiteUsuarios: this.limiteUsuarios(),
      confirmarExcedente: confirmar
    }).subscribe({
      next: () => { this.salvando.set(false); this.carregar(); this.fechar(); },
      error: e => this.falhou(e)
    });
  }

  atribuirPlano(confirmar = false) {
    const id = this.editando();
    const plano = this.planoEscolhido();
    if (id === null || plano === null) return;

    this.salvando.set(true);
    this.erroEdicao.set('');
    this.acaoDoExcedente.set('plano');
    if (confirmar) this.excedente.set('');

    this.servico.atribuirPlano(id, plano, confirmar).subscribe({
      next: () => { this.salvando.set(false); this.carregar(); this.fechar(); },
      error: e => this.falhou(e)
    });
  }

  /** Reenvia A MESMA ação que levou o 409, e não uma escolhida no template. Um `(click)` fixo ali
   *  volta a ser o defeito: o botão é um só e as ações são duas. */
  confirmarExcedente() {
    if (this.acaoDoExcedente() === 'plano') this.atribuirPlano(true);
    else this.salvarLimites(true);
  }

  cancelarExcedente() {
    this.excedente.set('');
    this.acaoDoExcedente.set(null);
  }

  alternarAtiva(e: EmpresaNaLista) {
    this.salvando.set(true);
    this.servico.definirAtiva(e.id, !e.ativa).subscribe({
      next: () => { this.salvando.set(false); this.carregar(); },
      error: err => this.falhou(err)
    });
  }

  /** O 409 do excedente vira um segundo passo; todo o resto é erro mesmo.
   *
   *  ⚠️ O 401 AQUI **NÃO** LIMPA A CHAVE, ao contrário da lista: há valores digitados na tela, e
   *  perdê-los por um caractere errado é o defeito que o interceptor já evita no resto do painel. */
  private falhou(e: { status?: number; error?: { erro?: string } }) {
    this.salvando.set(false);
    const msg = e.error?.erro ?? 'Não foi possível salvar.';
    if (e.status === 409 && msg.includes('Reenvie com confirmação')) this.excedente.set(msg);
    else this.erroEdicao.set(msg);
  }
}
