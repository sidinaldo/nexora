import { CurrencyPipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { OperadorServico } from '../../../nucleo/servicos/operador.servico';
import { PlanoDto } from '../../../nucleo/modelos';

/** O catálogo comercial (OPE-1).
 *
 *  ===================== O QUE ESTA TELA PRECISA DIZER EM PALAVRAS =====================
 *  Duas coisas, e as duas contrariam a leitura natural:
 *
 *    1. **o preço não cobra nada.** Não existe cobrança neste sistema — nem assinatura, nem
 *       gateway, nem fatura. É o registro do que foi combinado, lido por uma pessoa. Um campo de
 *       preço ao lado de um botão "atribuir" se lê como se mudasse o que o cliente paga;
 *
 *    2. **editar um plano não mexe em quem já está nele.** Os limites foram COPIADOS para a linha
 *       de cada empresa no momento da atribuição. Quem espera o contrário muda um plano achando
 *       que está dando mais conexões a dez clientes, e não dá a nenhum.
 *  =====================================================================================
 *
 *  ⚠️ NÃO HÁ APAGAR, só arquivar. `empresas.plano_id` é o único traço do que foi vendido, e uma
 *  empresa cujo plano sumiu fica com limites sem explicação. */
@Component({
  selector: 'app-operacao-planos',
  imports: [FormsModule, CurrencyPipe],
  templateUrl: './planos.html',
  styleUrl: './planos.css'
})
export class OperacaoPlanos implements OnInit {
  private servico = inject(OperadorServico);

  itens = signal<PlanoDto[]>([]);
  carregando = signal(false);
  erro = signal('');
  salvando = signal(false);

  /** Null = ninguém em edição. 0 = criando um novo. */
  editando = signal<number | null>(null);

  nome = signal('');
  preco = signal(0);
  limiteConexoes = signal(1);
  limiteUsuarios = signal(3);
  ordem = signal(1);
  ativo = signal(true);

  ngOnInit() { this.carregar(); }

  carregar() {
    this.carregando.set(true);
    this.erro.set('');
    this.servico.planos().subscribe({
      next: p => { this.itens.set(p); this.carregando.set(false); },
      error: e => {
        this.carregando.set(false);
        this.erro.set(e.error?.erro ?? 'Não foi possível carregar o catálogo.');
      }
    });
  }

  novo() {
    this.editando.set(0);
    this.nome.set('');
    this.preco.set(0);
    this.limiteConexoes.set(1);
    this.limiteUsuarios.set(3);
    this.ordem.set(this.itens().length + 1);
    this.ativo.set(true);
    this.erro.set('');
  }

  editar(p: PlanoDto) {
    this.editando.set(p.id);
    this.nome.set(p.nome);
    this.preco.set(p.preco);
    this.limiteConexoes.set(p.limiteConexoes);
    this.limiteUsuarios.set(p.limiteUsuarios);
    this.ordem.set(p.ordem);
    this.ativo.set(p.ativo);
    this.erro.set('');
  }

  fechar() { this.editando.set(null); }

  salvar() {
    const id = this.editando();
    if (id === null || !this.nome().trim()) return;

    this.salvando.set(true);
    this.erro.set('');

    const dados = {
      nome: this.nome().trim(),
      preco: this.preco(),
      limiteConexoes: this.limiteConexoes(),
      limiteUsuarios: this.limiteUsuarios(),
      ordem: this.ordem()
    };

    // `Observable<unknown>`: criar devolve o id e editar devolve vazio, e a tela não usa nem um
    // nem outro — ela recarrega a lista. Unir os dois tipos aqui evita um ramo duplicado só para
    // descartar valores diferentes.
    const pedido: Observable<unknown> = id === 0
      ? this.servico.criarPlano(dados)
      : this.servico.atualizarPlano(id, { ...dados, ativo: this.ativo() });

    pedido.subscribe({
      next: () => { this.salvando.set(false); this.fechar(); this.carregar(); },
      error: (e: { error?: { erro?: string } }) => {
        this.salvando.set(false);
        this.erro.set(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }
}
